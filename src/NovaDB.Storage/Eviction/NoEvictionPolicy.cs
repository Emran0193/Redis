using NovaDB.Core.Models;

namespace NovaDB.Storage.Eviction;

/// <summary>
/// Eviction policy that rejects writes when memory is over limit.
/// </summary>
public sealed class NoEvictionPolicy : IEvictionPolicy
{
    /// <inheritdoc />
    public bool TrySelectKey(IReadOnlyList<EvictionCandidate> candidates, out RedisKey selectedKey)
    {
        _ = candidates;
        selectedKey = default;
        return false;
    }
}
