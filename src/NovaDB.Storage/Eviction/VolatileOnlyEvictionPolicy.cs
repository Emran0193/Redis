using NovaDB.Core.Models;

namespace NovaDB.Storage.Eviction;

/// <summary>
/// Restricts an inner policy to keys that currently have a TTL (Redis volatile-* policies).
/// </summary>
public sealed class VolatileOnlyEvictionPolicy : IEvictionPolicy
{
    private readonly IEvictionPolicy _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="VolatileOnlyEvictionPolicy"/> class.
    /// </summary>
    public VolatileOnlyEvictionPolicy(IEvictionPolicy inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public bool TrySelectKey(IReadOnlyList<EvictionCandidate> candidates, out RedisKey selectedKey)
    {
        var volatileOnly = new List<EvictionCandidate>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].ExpireAtUnixMs is not null)
            {
                volatileOnly.Add(candidates[i]);
            }
        }

        if (volatileOnly.Count == 0)
        {
            selectedKey = default;
            return false;
        }

        return _inner.TrySelectKey(volatileOnly, out selectedKey);
    }
}
