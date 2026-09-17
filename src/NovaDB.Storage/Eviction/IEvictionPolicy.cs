using NovaDB.Core.Models;

namespace NovaDB.Storage.Eviction;

/// <summary>
/// Metadata about a stored key used by eviction policies.
/// </summary>
/// <param name="Key">Stored key.</param>
/// <param name="LastAccessTicks">Last access timestamp ticks.</param>
/// <param name="AccessFrequency">Access frequency counter.</param>
/// <param name="ExpireAtUnixMs">Absolute expiration in Unix milliseconds, if any.</param>
/// <param name="EstimatedMemoryBytes">Estimated memory usage for the key and value.</param>
public readonly record struct EvictionCandidate(
    RedisKey Key,
    long LastAccessTicks,
    long AccessFrequency,
    long? ExpireAtUnixMs,
    long EstimatedMemoryBytes);

/// <summary>
/// Selects keys to evict when memory limits are exceeded.
/// </summary>
public interface IEvictionPolicy
{
    /// <summary>
    /// Attempts to select a key to evict from the provided candidates.
    /// </summary>
    /// <param name="candidates">Snapshot of evictable keys.</param>
    /// <param name="selectedKey">Selected key when successful.</param>
    /// <returns><see langword="true"/> when a key was selected.</returns>
    bool TrySelectKey(IReadOnlyList<EvictionCandidate> candidates, out RedisKey selectedKey);
}
