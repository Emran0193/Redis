using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Monitoring;

namespace NovaDB.Networking;

/// <summary>
/// Tracks active TCP connections and enforces <see cref="NovaDbOptions.MaxConnections"/>.
/// </summary>
public sealed class ConnectionManager
{
    private int _activeCount;
    private readonly int _maxConnections;
    private readonly INovaDbMetrics _metrics;
    private readonly ILogger<ConnectionManager> _logger;
    private readonly ConcurrentDictionary<string, IClientConnection> _connections = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionManager"/> class.
    /// </summary>
    public ConnectionManager(
        IOptions<NovaDbOptions> options,
        INovaDbMetrics metrics,
        ILogger<ConnectionManager> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(logger);

        _maxConnections = options.Value.MaxConnections;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>Gets the current number of active connections.</summary>
    public int ActiveCount => Volatile.Read(ref _activeCount);

    /// <summary>Gets the configured maximum concurrent connections.</summary>
    public int MaxConnections => _maxConnections;

    /// <summary>
    /// Attempts to register a new connection when capacity is available.
    /// </summary>
    public bool TryAcquire()
    {
        while (true)
        {
            var current = Volatile.Read(ref _activeCount);
            if (current >= _maxConnections)
            {
                _logger.LogWarning(
                    "Connection rejected: active {ActiveCount} reached max {MaxConnections}",
                    current,
                    _maxConnections);
                return false;
            }

            if (Interlocked.CompareExchange(ref _activeCount, current + 1, current) == current)
            {
                _metrics.IncrementConnectedClients();
                _logger.LogDebug("Connection acquired: active {ActiveCount}/{MaxConnections}", current + 1, _maxConnections);
                return true;
            }
        }
    }

    /// <summary>Associates a live connection object for CLIENT LIST / INFO.</summary>
    public void Track(IClientConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _connections[connection.ConnectionId] = connection;
    }

    /// <summary>Removes a connection from the live directory.</summary>
    public void Untrack(string connectionId)
    {
        if (!string.IsNullOrWhiteSpace(connectionId))
        {
            _connections.TryRemove(connectionId, out _);
        }
    }

    /// <summary>Returns a snapshot of currently tracked connections.</summary>
    public IReadOnlyList<IClientConnection> GetConnections()
        => _connections.Values.ToArray();

    /// <summary>
    /// Releases a previously acquired connection slot.
    /// </summary>
    public void Release()
    {
        var remaining = Interlocked.Decrement(ref _activeCount);
        _metrics.DecrementConnectedClients();
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _activeCount, 0);
            _logger.LogError("Connection release underflow detected; counter reset to zero.");
            return;
        }

        _logger.LogDebug("Connection released: active {ActiveCount}/{MaxConnections}", remaining, _maxConnections);
    }

    /// <summary>
    /// Waits until all active connections have been released or the timeout elapses.
    /// </summary>
    public async Task WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (ActiveCount == 0)
        {
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (ActiveCount > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(50, timeoutCts.Token).ConfigureAwait(false);
        }
    }
}
