using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Core.Exceptions;
using NovaDB.Protocol;

namespace NovaDB.Commands;

/// <summary>
/// Shared AUTH password check with constant-time compare and per-connection backoff.
/// </summary>
public sealed class AuthPasswordVerifier
{
    private readonly IOptions<NovaDbOptions> _options;
    private readonly AuthRateLimiter _rateLimiter;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthPasswordVerifier"/> class.
    /// </summary>
    public AuthPasswordVerifier(IOptions<NovaDbOptions> options, AuthRateLimiter rateLimiter)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _rateLimiter = rateLimiter ?? throw new ArgumentNullException(nameof(rateLimiter));
    }

    /// <summary>
    /// Attempts authentication for a connection. Throws <see cref="AuthenticationException"/> on failure.
    /// </summary>
    /// <param name="connectionId">Client connection id.</param>
    /// <param name="password">Presented password.</param>
    /// <param name="remoteAddress">Optional remote IP for cross-connection lockout.</param>
    public void AuthenticateOrThrow(string connectionId, string password, string? remoteAddress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(password);

        var lockout = _rateLimiter.GetLockoutRemaining(connectionId, remoteAddress);
        if (lockout > TimeSpan.Zero)
        {
            var seconds = Math.Max(1, (int)Math.Ceiling(lockout.TotalSeconds));
            throw new AuthenticationException(
                $"too many authentication failures, retry after {seconds.ToString(CultureInfo.InvariantCulture)} second(s)");
        }

        if (!_options.Value.AuthenticationRequired)
        {
            _rateLimiter.RecordSuccess(connectionId, remoteAddress);
            return;
        }

        var expected = Encoding.UTF8.GetBytes(_options.Value.Password);
        var actual = Encoding.UTF8.GetBytes(password);
        // Pad/truncate comparison length carefully: FixedTimeEquals requires equal lengths.
        // Compare against equal-length buffers so timing does not leak password length.
        var max = Math.Max(expected.Length, actual.Length);
        var expectedPadded = new byte[max];
        var actualPadded = new byte[max];
        expected.CopyTo(expectedPadded, 0);
        actual.CopyTo(actualPadded, 0);

        var lengthMatch = expected.Length == actual.Length;
        var contentMatch = CryptographicOperations.FixedTimeEquals(expectedPadded, actualPadded);
        if (lengthMatch && contentMatch)
        {
            _rateLimiter.RecordSuccess(connectionId, remoteAddress);
            return;
        }

        var wait = _rateLimiter.RecordFailure(connectionId, remoteAddress);
        var waitSeconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        throw new AuthenticationException(
            $"invalid password, retry after {waitSeconds.ToString(CultureInfo.InvariantCulture)} second(s)");
    }
}
