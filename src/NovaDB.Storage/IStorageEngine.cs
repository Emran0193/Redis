using NovaDB.Core.Models;

namespace NovaDB.Storage;

/// <summary>
/// Abstraction over the in-memory NovaDB key-value store.
/// </summary>
public interface IStorageEngine
{
    /// <summary>Raised when a key is removed due to expiration.</summary>
    event Action<RedisKey>? KeyExpired;

    /// <summary>Raised when a key is created or mutated (used by WATCH).</summary>
    event Action<RedisKey>? KeyChanged;

    /// <summary>Gets the approximate number of keys in the database.</summary>
    long KeyCount { get; }

    /// <summary>Gets the estimated memory usage in bytes.</summary>
    long EstimatedMemoryBytes { get; }

    /// <summary>
    /// Retrieves an entry, applying lazy expiration when needed.
    /// </summary>
    /// <param name="key">Key to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The entry, or null when the key is missing or expired.</returns>
    ValueTask<DatabaseEntry?> GetAsync(RedisKey key, CancellationToken ct);

    /// <summary>
    /// Stores or replaces an entry under the given key.
    /// </summary>
    /// <param name="key">Target key.</param>
    /// <param name="entry">Entry payload.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask SetAsync(RedisKey key, DatabaseEntry entry, CancellationToken ct);

    /// <summary>
    /// Runs a read-modify-write mutation for a single key while holding that key's shard lock.
    /// </summary>
    /// <typeparam name="TState">Caller-supplied state type.</typeparam>
    /// <typeparam name="TResult">Mutation result type.</typeparam>
    /// <param name="key">Target key.</param>
    /// <param name="state">State handed to the mutation delegate.</param>
    /// <param name="mutation">Synchronous mutation delegate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The value returned by the mutation delegate.</returns>
    /// <remarks>
    /// This is the only safe way to mutate an existing value object. Reading via
    /// <see cref="GetAsync"/> and writing back with <see cref="SetAsync"/> races with
    /// concurrent commands on the same key.
    /// </remarks>
    ValueTask<TResult> MutateAsync<TState, TResult>(
        RedisKey key,
        TState state,
        StorageMutation<TState, TResult> mutation,
        CancellationToken ct);

    /// <summary>
    /// Deletes a key when present.
    /// </summary>
    /// <param name="key">Key to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> when a key was removed.</returns>
    ValueTask<bool> DeleteAsync(RedisKey key, CancellationToken ct);

    /// <summary>
    /// Returns whether the key exists and is not expired.
    /// </summary>
    /// <param name="key">Key to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>1 when the key exists, otherwise 0.</returns>
    ValueTask<long> ExistsAsync(RedisKey key, CancellationToken ct);

    /// <summary>
    /// Enumerates all non-expired keys and entries.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<(RedisKey Key, DatabaseEntry Entry)> ScanAsync(CancellationToken ct);

    /// <summary>
    /// Scans a page of keys using an opaque cursor that advances shard-by-shard (O(page), not O(N)).
    /// </summary>
    /// <param name="cursor">0 to start; otherwise a value returned by a prior call.</param>
    /// <param name="count">Hint for how many keys to examine in this page.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Next cursor (0 when finished) and the keys examined in this page.</returns>
    ValueTask<(long NextCursor, IReadOnlyList<RedisKey> Keys)> ScanKeysAsync(
        long cursor,
        int count,
        CancellationToken ct);

    /// <summary>
    /// Atomically increments a string value.
    /// </summary>
    /// <param name="key">Target key.</param>
    /// <param name="delta">Amount to add.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The value after increment.</returns>
    ValueTask<long> IncrementAsync(RedisKey key, long delta, CancellationToken ct);

    /// <summary>
    /// Returns the optimistic concurrency version for WATCH.
    /// </summary>
    /// <param name="key">Target key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Monotonic generation for the key. Survives deletion so a delete-and-recreate cannot
    /// trick WATCH into seeing the same version (ABA). Returns 0 only for a key that has never
    /// existed since process start.
    /// </returns>
    ValueTask<long> GetKeyVersionAsync(RedisKey key, CancellationToken ct);

    /// <summary>
    /// Runs <paramref name="action"/> while holding an exclusive database lock so no other
    /// storage operation (including reads) can interleave. Used to make EXEC atomic.
    /// </summary>
    /// <typeparam name="TResult">Result type.</typeparam>
    /// <param name="action">Work to run exclusively. Must not call back into storage on another thread.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The action result.</returns>
    ValueTask<TResult> RunExclusiveAsync<TResult>(
        Func<CancellationToken, ValueTask<TResult>> action,
        CancellationToken ct);
}
