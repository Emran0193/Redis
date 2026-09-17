using NovaDB.Core.Models;

namespace NovaDB.Storage.Eviction;

/// <summary>
/// Eviction policy that can be swapped at runtime (CONFIG SET maxmemory-policy).
/// </summary>
public sealed class ConfigurableEvictionPolicy : IEvictionPolicy
{
    private volatile IEvictionPolicy _inner = null!;
    private volatile string _policyName = null!;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurableEvictionPolicy"/> class.
    /// </summary>
    public ConfigurableEvictionPolicy(string initialPolicyName)
    {
        Replace(initialPolicyName);
    }

    /// <summary>Gets the active policy name.</summary>
    public string PolicyName => _policyName;

    /// <summary>Replaces the active eviction strategy.</summary>
    public void Replace(string policyName)
    {
        var normalized = string.IsNullOrWhiteSpace(policyName) ? "noeviction" : policyName.Trim();
        _inner = EvictionPolicyFactory.Create(normalized);
        _policyName = normalized;
    }

    /// <inheritdoc />
    public bool TrySelectKey(IReadOnlyList<EvictionCandidate> candidates, out RedisKey selectedKey)
        => _inner.TrySelectKey(candidates, out selectedKey);
}

/// <summary>Creates concrete eviction policies from Redis-compatible names.</summary>
public static class EvictionPolicyFactory
{
    /// <summary>Builds a policy for the given name (unknown names → noeviction).</summary>
    public static IEvictionPolicy Create(string policyName)
    {
        if (string.IsNullOrWhiteSpace(policyName))
        {
            return new NoEvictionPolicy();
        }

        return policyName.Trim().ToLowerInvariant() switch
        {
            "noeviction" => new NoEvictionPolicy(),
            "lru" or "allkeys-lru" or "allkeys-random" => new LruEvictionPolicy(),
            "lfu" or "allkeys-lfu" => new LfuEvictionPolicy(),
            "ttl" => new TtlEvictionPolicy(),
            "volatile-lru" or "volatile-random" => new VolatileOnlyEvictionPolicy(new LruEvictionPolicy()),
            "volatile-lfu" => new VolatileOnlyEvictionPolicy(new LfuEvictionPolicy()),
            "volatile-ttl" => new VolatileOnlyEvictionPolicy(new TtlEvictionPolicy()),
            _ => new NoEvictionPolicy()
        };
    }

    /// <summary>Returns whether <paramref name="policyName"/> is a recognized policy.</summary>
    public static bool IsKnown(string policyName)
    {
        if (string.IsNullOrWhiteSpace(policyName))
        {
            return false;
        }

        return policyName.Trim().ToLowerInvariant() is
            "noeviction" or "lru" or "lfu" or "ttl"
            or "allkeys-lru" or "allkeys-lfu" or "allkeys-random"
            or "volatile-lru" or "volatile-lfu" or "volatile-ttl" or "volatile-random";
    }
}
