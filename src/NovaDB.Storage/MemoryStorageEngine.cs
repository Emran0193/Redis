using NovaDB.Core.Exceptions;
using NovaDB.Core.Models;
using NovaDB.Storage.Eviction;
using NovaDB.Storage.Expiration;
using NovaDB.Storage.Values;

namespace NovaDB.Storage;

/// <summary>
/// Sharded in-memory storage engine with striped locking and lazy expiration.
/// </summary>
/// <remarks>
/// Locking rules: a shard lock is never held while acquiring another shard lock, and change
/// notifications are always raised after the lock is released. Eviction therefore samples shards
/// one at a time from outside any shard lock.
/// <para>
/// Ordinary reads and mutations take only their shard lock so concurrent clients scale across
/// <see cref="NovaDB.Configuration.NovaDbOptions.ShardCount"/> shards. <see cref="RunExclusiveAsync"/>
/// sets an exclusive flag (used by EXEC / snapshot / AOF rewrite); other commands spin-wait on that
/// flag so transactions stay atomic without serialising the entire data path behind a mutex.
/// </para>
/// </remarks>
public sealed class MemoryStorageEngine : IStorageEngine
{
    private const int EvictionSampleSize = 16;

    private readonly Shard[] _shards;
    private readonly IEvictionPolicy _evictionPolicy;
    private readonly IExpirationScheduler _expirationScheduler;
    private readonly IStorageMetrics _metrics;
    private long _memoryLimitBytes;
    private readonly SemaphoreSlim _exclusive = new(1, 1);
    private readonly AsyncLocal<int> _exclusiveDepth = new();
    private int _exclusiveHeld;
    private long _estimatedMemoryBytes;
    private long _keyCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryStorageEngine"/> class.
    /// </summary>
    /// <param name="options">Database options.</param>
    /// <param name="evictionPolicy">Eviction policy implementation.</param>
    /// <param name="expirationScheduler">Expiration scheduler.</param>
    /// <param name="metrics">Optional storage metrics sink.</param>
    public MemoryStorageEngine(
        Microsoft.Extensions.Options.IOptions<NovaDB.Configuration.NovaDbOptions> options,
        IEvictionPolicy evictionPolicy,
        IExpirationScheduler expirationScheduler,
        IStorageMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _evictionPolicy = evictionPolicy ?? throw new ArgumentNullException(nameof(evictionPolicy));
        _expirationScheduler = expirationScheduler ?? throw new ArgumentNullException(nameof(expirationScheduler));
        _metrics = metrics ?? NullStorageMetrics.Instance;

        var shardCount = options.Value.ShardCount;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(shardCount);

        _memoryLimitBytes = options.Value.MemoryLimitBytes;
        _shards = new Shard[shardCount];
        for (var i = 0; i < shardCount; i++)
        {
            _shards[i] = new Shard();
        }
    }

    /// <inheritdoc />
    public event Action<RedisKey>? KeyExpired;

    /// <inheritdoc />
    public event Action<RedisKey>? KeyChanged;

    /// <inheritdoc />
    public long KeyCount => Volatile.Read(ref _keyCount);

    /// <inheritdoc />
    public long EstimatedMemoryBytes => Volatile.Read(ref _estimatedMemoryBytes);

    /// <inheritdoc />
    public ValueTask<DatabaseEntry?> GetAsync(RedisKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnterDb();
        try
        {
            var shard = GetShard(key);
            DatabaseEntry? result = null;
            var expired = false;

            lock (shard.Sync)
            {
                if (shard.Entries.TryGetValue(key, out var entry))
                {
                    if (entry.IsExpired(CurrentUnixMs()))
                    {
                        RemoveEntryLocked(shard, key, entry);
                        expired = true;
                    }
                    else
                    {
                        TouchEntry(entry);
                        result = entry;
                    }
                }
            }

            if (expired)
            {
                NotifyExpired(key);
            }

            return ValueTask.FromResult(result);
        }
        finally
        {
            ExitDb();
        }
    }

    /// <inheritdoc />
    public ValueTask SetAsync(RedisKey key, DatabaseEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ct.ThrowIfCancellationRequested();

        Mutate(
            key,
            entry,
            static (newEntry, scope) =>
            {
                scope.Replace(newEntry);
                return true;
            },
            EntryMemoryEstimator.Estimate(key, entry));

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<TResult> MutateAsync<TState, TResult>(
        RedisKey key,
        TState state,
        StorageMutation<TState, TResult> mutation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Mutate(key, state, mutation));
    }

    private TResult Mutate<TState, TResult>(
        RedisKey key,
        TState state,
        StorageMutation<TState, TResult> mutation,
        long incomingBytes = 0)
    {
        EnterDb();
        try
        {
            EnsureMemoryAvailable(key, incomingBytes);

            var shard = GetShard(key);
            TResult result;
            var expired = false;
            MutationOutcome outcome;
            DatabaseEntry? stored = null;

            lock (shard.Sync)
            {
                shard.Entries.TryGetValue(key, out var existing);
                if (existing is not null && existing.IsExpired(CurrentUnixMs()))
                {
                    RemoveEntryLocked(shard, key, existing);
                    existing = null;
                    expired = true;
                }

                var oldSize = existing is null ? 0L : EntryMemoryEstimator.Estimate(key, existing);
                var scope = new MutationScope(existing);
                result = mutation(state, scope);
                outcome = scope.Outcome;

                switch (outcome)
                {
                    case MutationOutcome.InPlace:
                        stored = existing!;
                        stored.Version = BumpGenerationLocked(shard, key);
                        TouchEntry(stored);
                        AddMemory(EntryMemoryEstimator.Estimate(key, stored) - oldSize);
                        break;

                    case MutationOutcome.Replaced:
                        stored = scope.Replacement!;
                        ApplyReplacementLocked(shard, key, existing, stored, oldSize);
                        break;

                    case MutationOutcome.Removed:
                        if (existing is not null)
                        {
                            RemoveEntryLocked(shard, key, existing);
                        }
                        else
                        {
                            BumpGenerationLocked(shard, key);
                        }

                        break;

                    case MutationOutcome.None:
                    default:
                        break;
                }
            }

            if (expired)
            {
                NotifyExpired(key);
            }

            if (outcome is MutationOutcome.InPlace or MutationOutcome.Replaced)
            {
                UpdateExpirationSchedule(key, stored!);
                KeyChanged?.Invoke(key);
            }
            else if (outcome == MutationOutcome.Removed)
            {
                _expirationScheduler.Cancel(key);
                KeyChanged?.Invoke(key);
            }

            return result;
        }
        finally
        {
            ExitDb();
        }
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(RedisKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnterDb();
        try
        {
            var shard = GetShard(key);
            lock (shard.Sync)
            {
                if (!shard.Entries.TryGetValue(key, out var entry))
                {
                    return ValueTask.FromResult(false);
                }

                RemoveEntryLocked(shard, key, entry);
            }

            _expirationScheduler.Cancel(key);
            KeyChanged?.Invoke(key);
            return ValueTask.FromResult(true);
        }
        finally
        {
            ExitDb();
        }
    }

    /// <inheritdoc />
    public ValueTask<long> ExistsAsync(RedisKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnterDb();
        try
        {
            var shard = GetShard(key);
            var exists = 0L;
            var expired = false;

            lock (shard.Sync)
            {
                if (shard.Entries.TryGetValue(key, out var entry))
                {
                    if (entry.IsExpired(CurrentUnixMs()))
                    {
                        RemoveEntryLocked(shard, key, entry);
                        expired = true;
                    }
                    else
                    {
                        TouchEntry(entry);
                        exists = 1L;
                    }
                }
            }

            if (expired)
            {
                NotifyExpired(key);
            }

            return ValueTask.FromResult(exists);
        }
        finally
        {
            ExitDb();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(RedisKey Key, DatabaseEntry Entry)> ScanAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var shardIndex = 0; shardIndex < _shards.Length; shardIndex++)
        {
            ct.ThrowIfCancellationRequested();
            var shard = _shards[shardIndex];
            List<(RedisKey Key, DatabaseEntry Entry)> snapshot;

            EnterDb();
            try
            {
                lock (shard.Sync)
                {
                    snapshot = new List<(RedisKey Key, DatabaseEntry Entry)>(shard.Entries.Count);
                    foreach (var pair in shard.Entries)
                    {
                        if (pair.Value.IsExpired(CurrentUnixMs()))
                        {
                            continue;
                        }

                        snapshot.Add((pair.Key, pair.Value));
                    }
                }
            }
            finally
            {
                ExitDb();
            }

            for (var i = 0; i < snapshot.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return snapshot[i];
                await Task.Yield();
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<(long NextCursor, IReadOnlyList<RedisKey> Keys)> ScanKeysAsync(
        long cursor,
        int count,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (count <= 0)
        {
            count = 10;
        }

        var shardCount = _shards.Length;
            var shardIndex = (int)((ulong)cursor >> 32);
        var offset = (int)(cursor & 0xFFFFFFFFL);
        if (shardIndex < 0 || shardIndex >= shardCount)
        {
            return ValueTask.FromResult<(long, IReadOnlyList<RedisKey>)>((0, Array.Empty<RedisKey>()));
        }

        EnterDb();
        try
        {
            var keys = new List<RedisKey>(Math.Min(count, 64));
            while (shardIndex < shardCount && keys.Count < count)
            {
                var shard = _shards[shardIndex];
                List<RedisKey> snapshot;
                lock (shard.Sync)
                {
                    snapshot = new List<RedisKey>(shard.Entries.Count);
                    var now = CurrentUnixMs();
                    foreach (var pair in shard.Entries)
                    {
                        if (!pair.Value.IsExpired(now))
                        {
                            snapshot.Add(pair.Key);
                        }
                    }
                }

                while (offset < snapshot.Count && keys.Count < count)
                {
                    keys.Add(snapshot[offset++]);
                }

                if (offset >= snapshot.Count)
                {
                    shardIndex++;
                    offset = 0;
                }
                else
                {
                    break;
                }
            }

            long next = shardIndex >= shardCount
                ? 0L
                : ((long)shardIndex << 32) | (uint)offset;
            return ValueTask.FromResult<(long, IReadOnlyList<RedisKey>)>((next, keys));
        }
        finally
        {
            ExitDb();
        }
    }

    /// <inheritdoc />
    public ValueTask<long> IncrementAsync(RedisKey key, long delta, CancellationToken ct)
        => MutateAsync(
            key,
            delta,
            static (amount, scope) =>
            {
                if (scope.Entry is null)
                {
                    scope.Replace(new DatabaseEntry(RedisValueType.String, StringValue.FromInt64(amount)));
                    return amount;
                }

                if (scope.Entry.Type != RedisValueType.String || scope.Entry.Value is not StringValue stringValue)
                {
                    throw new WrongTypeException();
                }

                if (!stringValue.TryParseInt64(out var current))
                {
                    throw new NovaDbCommandException("value is not an integer or out of range");
                }

                var updated = current + amount;
                stringValue.SetInt64(updated);
                scope.MarkMutated();
                return updated;
            },
            ct);

    /// <inheritdoc />
    public ValueTask<long> GetKeyVersionAsync(RedisKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnterDb();
        try
        {
            var shard = GetShard(key);
            lock (shard.Sync)
            {
                return ValueTask.FromResult(
                    shard.Generations.TryGetValue(key, out var generation) ? generation : 0L);
            }
        }
        finally
        {
            ExitDb();
        }
    }

    /// <inheritdoc />
    public async ValueTask<TResult> RunExclusiveAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> action,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        ct.ThrowIfCancellationRequested();

        if (_exclusiveDepth.Value == 0)
        {
            await _exclusive.WaitAsync(ct).ConfigureAwait(false);
            _exclusiveDepth.Value = 1;
            Volatile.Write(ref _exclusiveHeld, 1);
        }
        else
        {
            _exclusiveDepth.Value++;
        }

        try
        {
            return await action(ct).ConfigureAwait(false);
        }
        finally
        {
            if (--_exclusiveDepth.Value == 0)
            {
                Volatile.Write(ref _exclusiveHeld, 0);
                _exclusive.Release();
            }
        }
    }

    /// <summary>
    /// Removes an expired key without raising <see cref="KeyChanged"/>.
    /// </summary>
    /// <param name="key">Expired key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when the key was removed.</returns>
    internal ValueTask<bool> RemoveExpiredKeyAsync(RedisKey key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnterDb();
        try
        {
            var shard = GetShard(key);
            var removed = false;
            lock (shard.Sync)
            {
                if (shard.Entries.TryGetValue(key, out var entry))
                {
                    RemoveEntryLocked(shard, key, entry);
                    removed = true;
                }
            }

            if (removed)
            {
                NotifyExpired(key);
            }

            return ValueTask.FromResult(removed);
        }
        finally
        {
            ExitDb();
        }
    }

    private void ApplyReplacementLocked(
        Shard shard,
        RedisKey key,
        DatabaseEntry? existing,
        DatabaseEntry replacement,
        long oldSize)
    {
        replacement.LastAccessTicks = Environment.TickCount64;
        var generation = BumpGenerationLocked(shard, key);
        replacement.Version = generation;

        if (existing is null)
        {
            replacement.AccessFrequency = 1;
            AddEntryLocked(shard, key, replacement);
            AddMemory(EntryMemoryEstimator.Estimate(key, replacement));
            return;
        }

        if (ReferenceEquals(existing, replacement))
        {
            AddMemory(EntryMemoryEstimator.Estimate(key, replacement) - oldSize);
            return;
        }

        replacement.AccessFrequency = existing.AccessFrequency + 1;
        replacement.StorageSlot = existing.StorageSlot;
        shard.Entries[key] = replacement;
        AddMemory(EntryMemoryEstimator.Estimate(key, replacement) - oldSize);
    }

    private void AddEntryLocked(Shard shard, RedisKey key, DatabaseEntry entry)
    {
        entry.StorageSlot = shard.KeyRing.Count;
        shard.KeyRing.Add(key);
        shard.Entries[key] = entry;
        Interlocked.Increment(ref _keyCount);
    }

    private static long BumpGenerationLocked(Shard shard, RedisKey key)
    {
        shard.Generations.TryGetValue(key, out var current);
        var next = current + 1;
        shard.Generations[key] = next;
        return next;
    }

    private void AddMemory(long delta)
    {
        if (delta != 0)
        {
            var current = Interlocked.Add(ref _estimatedMemoryBytes, delta);
            _metrics.SetMemoryBytes(current);
        }
    }

    private void NotifyExpired(RedisKey key)
    {
        _expirationScheduler.Cancel(key);
        _metrics.RecordExpiredKey();
        KeyExpired?.Invoke(key);
    }

    /// <summary>
    /// Brings memory usage back under the configured limit before a write is applied.
    /// </summary>
    /// <remarks>
    /// Runs outside any shard lock, which is what keeps eviction from deadlocking: reclaiming
    /// memory needs to touch other shards, and doing that while holding the writing shard's lock
    /// lets two concurrent writes acquire the same two locks in opposite orders.
    /// <para>
    /// When the size of the incoming write is known (a whole-entry <see cref="SetAsync"/>) the
    /// limit is enforced strictly and an unsatisfiable write is rejected. For in-place mutations
    /// the growth is not known in advance, so the limit behaves like Redis <c>maxmemory</c>: the
    /// write may overshoot and the next one reclaims the difference.
    /// </para>
    /// </remarks>
    /// <param name="protectedKey">Key being written, which is never chosen as an eviction victim.</param>
    /// <param name="incomingBytes">Known size of the incoming entry, or 0 when unknown.</param>
    private void EnsureMemoryAvailable(RedisKey protectedKey, long incomingBytes)
    {
        if (Volatile.Read(ref _memoryLimitBytes) <= 0)
        {
            return;
        }

        var replacedBytes = incomingBytes > 0 ? GetEntrySize(protectedKey) : 0;

        while (Volatile.Read(ref _estimatedMemoryBytes) + incomingBytes - replacedBytes > Volatile.Read(ref _memoryLimitBytes))
        {
            if (Volatile.Read(ref _memoryLimitBytes) <= 0)
            {
                return;
            }

            if (!TryEvictOne(protectedKey))
            {
                throw new OutOfMemoryNovaDbException();
            }
        }
    }

    /// <summary>
    /// Updates the live maxmemory limit (0 = unlimited). Used by CONFIG SET.
    /// </summary>
    public void SetMemoryLimitBytes(long memoryLimitBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(memoryLimitBytes);
        Interlocked.Exchange(ref _memoryLimitBytes, memoryLimitBytes);
    }

    /// <summary>Gets the live maxmemory limit in bytes (0 = unlimited).</summary>
    public long MemoryLimitBytes => Volatile.Read(ref _memoryLimitBytes);

    private long GetEntrySize(RedisKey key)
    {
        var shard = GetShard(key);
        lock (shard.Sync)
        {
            return shard.Entries.TryGetValue(key, out var entry)
                ? EntryMemoryEstimator.Estimate(key, entry)
                : 0;
        }
    }

    private bool TryEvictOne(RedisKey protectedKey)
    {
        var candidates = SampleEvictionCandidates(protectedKey);
        if (candidates.Count == 0 || !_evictionPolicy.TrySelectKey(candidates, out var selectedKey))
        {
            return false;
        }

        var shard = GetShard(selectedKey);
        var evicted = false;
        lock (shard.Sync)
        {
            if (shard.Entries.TryGetValue(selectedKey, out var entry))
            {
                RemoveEntryLocked(shard, selectedKey, entry);
                evicted = true;
            }
        }

        if (!evicted)
        {
            return candidates.Count > 1;
        }

        _expirationScheduler.Cancel(selectedKey);
        _metrics.RecordEviction();
        KeyChanged?.Invoke(selectedKey);
        return true;
    }

    /// <summary>
    /// Samples a bounded set of eviction candidates, locking one shard at a time.
    /// </summary>
    private List<EvictionCandidate> SampleEvictionCandidates(RedisKey protectedKey)
    {
        var candidates = new List<EvictionCandidate>(EvictionSampleSize);
        var attempts = EvictionSampleSize * 4;

        for (var attempt = 0; attempt < attempts && candidates.Count < EvictionSampleSize; attempt++)
        {
            var shard = _shards[Random.Shared.Next(_shards.Length)];

            lock (shard.Sync)
            {
                if (shard.KeyRing.Count == 0)
                {
                    continue;
                }

                var key = shard.KeyRing[Random.Shared.Next(shard.KeyRing.Count)];
                if (key.Equals(protectedKey) || !shard.Entries.TryGetValue(key, out var entry))
                {
                    continue;
                }

                candidates.Add(new EvictionCandidate(
                    key,
                    entry.LastAccessTicks,
                    entry.AccessFrequency,
                    entry.ExpireAtUnixMs,
                    EntryMemoryEstimator.Estimate(key, entry)));
            }
        }

        return candidates;
    }

    private void RemoveEntryLocked(Shard shard, RedisKey key, DatabaseEntry entry)
    {
        if (!shard.Entries.Remove(key))
        {
            return;
        }

        RemoveFromKeyRingLocked(shard, key, entry.StorageSlot);
        entry.StorageSlot = -1;
        BumpGenerationLocked(shard, key);

        var size = EntryMemoryEstimator.Estimate(key, entry);
        Interlocked.Decrement(ref _keyCount);
        AddMemory(-size);
    }

    private static void RemoveFromKeyRingLocked(Shard shard, RedisKey key, int slot)
    {
        if (slot < 0 || slot >= shard.KeyRing.Count || !shard.KeyRing[slot].Equals(key))
        {
            // Recorded slot is stale; fall back to a scan rather than corrupting the ring.
            slot = shard.KeyRing.IndexOf(key);
            if (slot < 0)
            {
                return;
            }
        }

        var lastIndex = shard.KeyRing.Count - 1;
        if (slot != lastIndex)
        {
            var moved = shard.KeyRing[lastIndex];
            shard.KeyRing[slot] = moved;
            if (shard.Entries.TryGetValue(moved, out var movedEntry))
            {
                movedEntry.StorageSlot = slot;
            }
        }

        shard.KeyRing.RemoveAt(lastIndex);
    }

    private void UpdateExpirationSchedule(RedisKey key, DatabaseEntry entry)
    {
        if (entry.ExpireAtUnixMs is long expireAt)
        {
            _expirationScheduler.Schedule(key, expireAt);
        }
        else
        {
            _expirationScheduler.Cancel(key);
        }
    }

    private static void TouchEntry(DatabaseEntry entry)
    {
        entry.LastAccessTicks = Environment.TickCount64;
        entry.AccessFrequency++;
    }

    /// <summary>
    /// Blocks briefly while an exclusive EXEC/snapshot/rewrite section is active; does not hold a
    /// process-wide mutex for the duration of ordinary commands.
    /// </summary>
    private void EnterDb()
    {
        if (_exclusiveDepth.Value > 0)
        {
            return;
        }

        while (Volatile.Read(ref _exclusiveHeld) != 0)
        {
            _exclusive.Wait();
            _exclusive.Release();
        }
    }

    private void ExitDb()
    {
        // Ordinary commands no longer hold the exclusive semaphore.
    }

    private Shard GetShard(RedisKey key)
    {
        var index = HashSlot.ComputeShardIndex(key.Bytes, _shards.Length);
        return _shards[index];
    }

    private static long CurrentUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class Shard
    {
        public Dictionary<RedisKey, DatabaseEntry> Entries { get; } = new(RedisKeyComparer.Instance);

        /// <summary>
        /// Gets the monotonic WATCH generation per key. Survives deletion so recreate cannot ABA.
        /// </summary>
        public Dictionary<RedisKey, long> Generations { get; } = new(RedisKeyComparer.Instance);

        /// <summary>Gets the key list used for O(1) random eviction sampling.</summary>
        public List<RedisKey> KeyRing { get; } = [];

        public object Sync { get; } = new();
    }
}
