using NovaDB.Core.Models;

namespace NovaDB.Storage.Eviction;

/// <summary>
/// Evicts the least recently used key.
/// </summary>
public sealed class LruEvictionPolicy : IEvictionPolicy
{
    /// <inheritdoc />
    public bool TrySelectKey(IReadOnlyList<EvictionCandidate> candidates, out RedisKey selectedKey)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        selectedKey = default;

        if (candidates.Count == 0)
        {
            return false;
        }

        var bestIndex = 0;
        var bestTicks = candidates[0].LastAccessTicks;
        for (var i = 1; i < candidates.Count; i++)
        {
            if (candidates[i].LastAccessTicks < bestTicks)
            {
                bestTicks = candidates[i].LastAccessTicks;
                bestIndex = i;
            }
        }

        selectedKey = candidates[bestIndex].Key;
        return true;
    }
}
