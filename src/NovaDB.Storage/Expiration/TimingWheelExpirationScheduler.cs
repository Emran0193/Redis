using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NovaDB.Core.Hosting;
using NovaDB.Core.Models;

namespace NovaDB.Storage.Expiration;

/// <summary>
/// Hierarchical timing wheel that schedules key expirations and processes them in the background.
/// </summary>
public sealed class TimingWheelExpirationScheduler : IExpirationScheduler, IHostedService, IDisposable
{
    private const int WheelSize = 256;
    private const int WheelLevels = 4;
    private const long TickMs = 50;

    private readonly Lazy<MemoryStorageEngine> _storage;
    private readonly IDatabaseReadiness? _readiness;
    private readonly ILogger<TimingWheelExpirationScheduler> _logger;
    private readonly object _wheelLock = new();
    private readonly WheelBucket[][] _wheels = new WheelBucket[WheelLevels][];
    private readonly Dictionary<RedisKey, ScheduledExpiration> _scheduled = new(RedisKeyComparer.Instance);
    private readonly Queue<ScheduledExpiration> _dueNow = new();
    private long _currentTick;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimingWheelExpirationScheduler"/> class.
    /// </summary>
    /// <param name="services">Service provider used to resolve <see cref="IStorageEngine"/> lazily.</param>
    /// <param name="logger">Logger instance.</param>
    public TimingWheelExpirationScheduler(IServiceProvider services, ILogger<TimingWheelExpirationScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _storage = new Lazy<MemoryStorageEngine>(services.GetRequiredService<MemoryStorageEngine>);
        _readiness = services.GetService<IDatabaseReadiness>();

        for (var level = 0; level < WheelLevels; level++)
        {
            _wheels[level] = new WheelBucket[WheelSize];
            for (var slot = 0; slot < WheelSize; slot++)
            {
                _wheels[level][slot] = new WheelBucket();
            }
        }
    }

    /// <inheritdoc />
    public void Schedule(RedisKey key, long expireAtUnixMs)
    {
        lock (_wheelLock)
        {
            if (_scheduled.TryGetValue(key, out var existing))
            {
                RemoveFromBucket(existing);
                _scheduled.Remove(key);
            }

            var expiration = new ScheduledExpiration(key, expireAtUnixMs);
            _scheduled[key] = expiration;
            PlaceInWheel(expiration, CurrentUnixMs());
        }
    }

    /// <inheritdoc />
    public void Cancel(RedisKey key)
    {
        lock (_wheelLock)
        {
            if (_scheduled.TryGetValue(key, out var existing))
            {
                RemoveFromBucket(existing);
                _scheduled.Remove(key);
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = RunAsync(_runCts.Token);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_runCts is null)
        {
            return;
        }

        await _runCts.CancelAsync().ConfigureAwait(false);
        if (_runTask is not null)
        {
            try
            {
                await _runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the run loop observes cancellation.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _runCts?.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (_readiness is not null && !_readiness.IsReady)
        {
            _logger.LogInformation("Expiration scheduler waiting for database recovery before ticking.");
            try
            {
                await _readiness.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Expiration scheduler not starting because recovery failed.");
                return;
            }
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickMs));
        _currentTick = CurrentUnixMs() / TickMs;

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            List<ScheduledExpiration> due;
            lock (_wheelLock)
            {
                _currentTick++;
                due = CollectDueExpirations();
            }

            for (var i = 0; i < due.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await ProcessExpirationAsync(due[i], ct).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask ProcessExpirationAsync(ScheduledExpiration expiration, CancellationToken ct)
    {
        var now = CurrentUnixMs();
        if (expiration.ExpireAtUnixMs > now)
        {
            lock (_wheelLock)
            {
                if (_scheduled.TryGetValue(expiration.Key, out var current) && current == expiration)
                {
                    PlaceInWheel(expiration, now);
                }
            }

            return;
        }

        lock (_wheelLock)
        {
            if (!_scheduled.TryGetValue(expiration.Key, out var current) || current != expiration)
            {
                return;
            }

            _scheduled.Remove(expiration.Key);
        }

        try
        {
            await _storage.Value.RemoveExpiredKeyAsync(expiration.Key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to remove expired key {Key}", expiration.Key);
        }
    }

    private List<ScheduledExpiration> CollectDueExpirations()
    {
        var due = new List<ScheduledExpiration>();
        ProcessImmediateQueue(due);

        var slot = (int)(_currentTick % WheelSize);
        var bucket = _wheels[0][slot];
        PromoteOrExpire(bucket, due, 0);

        return due;
    }

    private void ProcessImmediateQueue(List<ScheduledExpiration> due)
    {
        while (_dueNow.Count > 0)
        {
            due.Add(_dueNow.Dequeue());
        }
    }

    private void PromoteOrExpire(WheelBucket bucket, List<ScheduledExpiration> due, int level)
    {
        var node = bucket.Entries.First;
        while (node is not null)
        {
            var next = node.Next;
            var expiration = node.Value;
            bucket.Entries.Remove(node);

            if (!_scheduled.TryGetValue(expiration.Key, out var tracked) || tracked != expiration)
            {
                node = next;
                continue;
            }

            var now = CurrentUnixMs();
            if (expiration.ExpireAtUnixMs <= now)
            {
                _scheduled.Remove(expiration.Key);
                due.Add(expiration);
            }
            else if (level + 1 >= WheelLevels)
            {
                PlaceInWheel(expiration, now);
            }
            else
            {
                PromoteToLevel(expiration, level + 1, now);
            }

            node = next;
        }
    }

    private void PlaceInWheel(ScheduledExpiration expiration, long nowUnixMs)
    {
        var remainingMs = expiration.ExpireAtUnixMs - nowUnixMs;
        if (remainingMs <= 0)
        {
            _dueNow.Enqueue(expiration);
            return;
        }

        var ticksRemaining = remainingMs / TickMs;
        if (ticksRemaining <= 0)
        {
            _dueNow.Enqueue(expiration);
            return;
        }

        var level = 0;
        var span = (long)WheelSize;
        while (level < WheelLevels - 1 && ticksRemaining >= span)
        {
            ticksRemaining /= WheelSize;
            span *= WheelSize;
            level++;
        }

        var slot = (int)((_currentTick + ticksRemaining) % WheelSize);
        expiration.Level = level;
        expiration.BucketNode = _wheels[level][slot].Entries.AddLast(expiration);
    }

    private void PromoteToLevel(ScheduledExpiration expiration, int level, long nowUnixMs)
    {
        RemoveFromBucket(expiration);
        expiration.Level = level;
        var remainingMs = expiration.ExpireAtUnixMs - nowUnixMs;
        var ticksRemaining = remainingMs / TickMs;
        for (var i = 0; i < level; i++)
        {
            ticksRemaining /= WheelSize;
        }

        if (ticksRemaining <= 0)
        {
            _dueNow.Enqueue(expiration);
            return;
        }

        var slot = (int)((_currentTick + ticksRemaining) % WheelSize);
        expiration.BucketNode = _wheels[level][slot].Entries.AddLast(expiration);
    }

    private void RemoveFromBucket(ScheduledExpiration expiration)
    {
        if (expiration.BucketNode is not null)
        {
            expiration.BucketNode.List?.Remove(expiration.BucketNode);
            expiration.BucketNode = null;
        }
    }

    private static long CurrentUnixMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private sealed class WheelBucket
    {
        public LinkedList<ScheduledExpiration> Entries { get; } = new();
    }

    private sealed class ScheduledExpiration
    {
        public ScheduledExpiration(RedisKey key, long expireAtUnixMs)
        {
            Key = key;
            ExpireAtUnixMs = expireAtUnixMs;
        }

        public RedisKey Key { get; }

        public long ExpireAtUnixMs { get; }

        public int Level { get; set; }

        public LinkedListNode<ScheduledExpiration>? BucketNode { get; set; }
    }
}
