using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;

namespace NovaDB.Commands;

/// <summary>
/// AUTH failure backoff keyed by connection id and remote IP so reconnects cannot reset lockout.
/// </summary>
public sealed class AuthRateLimiter
{
    private readonly ConcurrentDictionary<string, AuthFailureState> _failures = new(StringComparer.Ordinal);
    private readonly TimeSpan _baseLockout;
    private readonly TimeSpan _maxLockout;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthRateLimiter"/> class.
    /// </summary>
    public AuthRateLimiter(IOptions<NovaDbOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _baseLockout = options.Value.AuthLockoutBase <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(1)
            : options.Value.AuthLockoutBase;
        _maxLockout = options.Value.AuthLockoutMax <= TimeSpan.Zero
            ? TimeSpan.FromMinutes(1)
            : options.Value.AuthLockoutMax;
    }

    /// <summary>
    /// Returns the remaining lockout for a connection/IP, or <see cref="TimeSpan.Zero"/> when free to AUTH.
    /// </summary>
    public TimeSpan GetLockoutRemaining(string connectionId, string? remoteAddress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        var remaining = RemainingFor(ConnectionKey(connectionId));
        if (!string.IsNullOrWhiteSpace(remoteAddress))
        {
            var ipRemaining = RemainingFor(IpKey(remoteAddress));
            if (ipRemaining > remaining)
            {
                remaining = ipRemaining;
            }
        }

        return remaining;
    }

    /// <summary>
    /// Clears failure state after a successful AUTH.
    /// </summary>
    public void RecordSuccess(string connectionId, string? remoteAddress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        _failures.TryRemove(ConnectionKey(connectionId), out _);
        if (!string.IsNullOrWhiteSpace(remoteAddress))
        {
            _failures.TryRemove(IpKey(remoteAddress), out _);
        }
    }

    /// <summary>
    /// Records a failed AUTH and returns how long the client must wait before retrying.
    /// </summary>
    public TimeSpan RecordFailure(string connectionId, string? remoteAddress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        var wait = ApplyFailure(ConnectionKey(connectionId));
        if (!string.IsNullOrWhiteSpace(remoteAddress))
        {
            var ipWait = ApplyFailure(IpKey(remoteAddress));
            if (ipWait > wait)
            {
                wait = ipWait;
            }
        }

        return wait;
    }

    /// <summary>
    /// Drops per-connection tracking for a disconnected client. IP lockout is retained.
    /// </summary>
    public void Forget(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        _failures.TryRemove(ConnectionKey(connectionId), out _);
    }

    private TimeSpan RemainingFor(string key)
    {
        if (!_failures.TryGetValue(key, out var state))
        {
            return TimeSpan.Zero;
        }

        var remaining = state.LockedUntilUtc - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private TimeSpan ApplyFailure(string key)
    {
        var state = _failures.AddOrUpdate(
            key,
            _ => CreateState(failureCount: 1),
            (_, existing) => CreateState(existing.FailureCount + 1));

        var remaining = state.LockedUntilUtc - DateTimeOffset.UtcNow;
        return remaining > TimeSpan.Zero ? remaining : _baseLockout;
    }

    private AuthFailureState CreateState(int failureCount)
    {
        // Exponential backoff: base * 2^(n-1), capped at max.
        var multiplier = 1L << Math.Min(failureCount - 1, 16);
        var lockoutTicks = Math.Min(_baseLockout.Ticks * multiplier, _maxLockout.Ticks);
        return new AuthFailureState(failureCount, DateTimeOffset.UtcNow + TimeSpan.FromTicks(lockoutTicks));
    }

    private static string ConnectionKey(string connectionId) => "c:" + connectionId;

    private static string IpKey(string remoteAddress) => "ip:" + remoteAddress;

    private readonly record struct AuthFailureState(int FailureCount, DateTimeOffset LockedUntilUtc);
}
