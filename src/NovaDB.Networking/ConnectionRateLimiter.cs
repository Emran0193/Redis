using Microsoft.Extensions.Options;
using NovaDB.Configuration;

namespace NovaDB.Networking;

/// <summary>
/// Token-bucket style limiter for new TCP accepts.
/// </summary>
public sealed class ConnectionRateLimiter
{
    private readonly IOptions<NovaDbOptions> _options;
    private readonly object _gate = new();
    private double _tokens;
    private long _lastTicks;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionRateLimiter"/> class.
    /// </summary>
    public ConnectionRateLimiter(IOptions<NovaDbOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokens = Math.Max(1, options.Value.ConnectionRateLimitPerSecond);
        _lastTicks = Environment.TickCount64;
    }

    /// <summary>
    /// Attempts to consume one accept permit.
    /// </summary>
    /// <returns><see langword="true"/> when the accept is allowed.</returns>
    public bool TryAcquire()
    {
        var limit = _options.Value.ConnectionRateLimitPerSecond;
        if (limit <= 0)
        {
            return true;
        }

        lock (_gate)
        {
            var now = Environment.TickCount64;
            var elapsedMs = Math.Max(0, now - _lastTicks);
            _lastTicks = now;
            _tokens = Math.Min(limit, _tokens + (elapsedMs / 1000.0) * limit);
            if (_tokens < 1)
            {
                return false;
            }

            _tokens -= 1;
            return true;
        }
    }
}
