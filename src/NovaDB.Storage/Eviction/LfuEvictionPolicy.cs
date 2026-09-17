using NovaDB.Core.Models;

namespace NovaDB.Storage.Eviction;

/// <summary>
/// Evicts the least frequently used key.
/// </summary>
public sealed class LfuEvictionPolicy : IEvictionPolicy
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
        var bestFrequency = candidates[0].AccessFrequency;
        var bestTicks = candidates[0].LastAccessTicks;
        for (var i = 1; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (candidate.AccessFrequency < bestFrequency
                || (candidate.AccessFrequency == bestFrequency && candidate.LastAccessTicks < bestTicks))
            {
                bestFrequency = candidate.AccessFrequency;
                bestTicks = candidate.LastAccessTicks;
                bestIndex = i;
            }
        }

        selectedKey = candidates[bestIndex].Key;
        return true;
    }
}
