using NovaDB.Core.Models;

namespace NovaDB.Storage.Eviction;

/// <summary>
/// Evicts keys with the nearest expiration time, preferring TTL keys over persistent keys.
/// </summary>
public sealed class TtlEvictionPolicy : IEvictionPolicy
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

        var bestIndex = -1;
        long bestExpire = long.MaxValue;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].ExpireAtUnixMs is not long expireAt)
            {
                continue;
            }

            if (expireAt < bestExpire)
            {
                bestExpire = expireAt;
                bestIndex = i;
            }
        }

        if (bestIndex >= 0)
        {
            selectedKey = candidates[bestIndex].Key;
            return true;
        }

        var fallbackIndex = 0;
        var fallbackTicks = candidates[0].LastAccessTicks;
        for (var i = 1; i < candidates.Count; i++)
        {
            if (candidates[i].LastAccessTicks < fallbackTicks)
            {
                fallbackTicks = candidates[i].LastAccessTicks;
                fallbackIndex = i;
            }
        }

        selectedKey = candidates[fallbackIndex].Key;
        return true;
    }
}
