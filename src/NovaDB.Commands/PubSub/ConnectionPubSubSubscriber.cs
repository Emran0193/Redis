using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NovaDB.Networking;
using NovaDB.Protocol;
using NovaDB.PubSub;

namespace NovaDB.Commands.PubSub;

/// <summary>
/// Adapts <see cref="IClientConnection"/> to <see cref="IPubSubSubscriber"/> with a bounded
/// outbox so a slow subscriber cannot stall publishers.
/// </summary>
public sealed class ConnectionPubSubSubscriber : IPubSubSubscriber, IAsyncDisposable
{
    /// <summary>Maximum queued push messages before the subscriber is considered slow.</summary>
    public const int DefaultOutboxCapacity = 1_024;

    private readonly IClientConnection _connection;
    private readonly ILogger<ConnectionPubSubSubscriber>? _logger;
    private readonly Channel<RespValue> _outbox;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _pump;
    private int _disposed;
    private int _overflowed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionPubSubSubscriber"/> class.
    /// </summary>
    /// <param name="connection">The client connection.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="outboxCapacity">Bounded outbox capacity.</param>
    public ConnectionPubSubSubscriber(
        IClientConnection connection,
        ILogger<ConnectionPubSubSubscriber>? logger = null,
        int outboxCapacity = DefaultOutboxCapacity)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger;
        _outbox = Channel.CreateBounded<RespValue>(new BoundedChannelOptions(Math.Max(16, outboxCapacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _pump = PumpAsync(_lifetime.Token);
    }

    /// <inheritdoc />
    public string ConnectionId => _connection.ConnectionId;

    /// <summary>
    /// Gets a value indicating whether the outbox overflowed and the subscriber should be dropped.
    /// </summary>
    public bool IsOverflowed => Volatile.Read(ref _overflowed) != 0;

    /// <inheritdoc />
    public ValueTask SendAsync(RespValue message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (Volatile.Read(ref _disposed) != 0 || IsOverflowed)
        {
            return ValueTask.CompletedTask;
        }

        // Never await a full outbox: publishers must not block on a slow client.
        if (_outbox.Writer.TryWrite(message))
        {
            return ValueTask.CompletedTask;
        }

        if (Interlocked.Exchange(ref _overflowed, 1) == 0)
        {
            _logger?.LogWarning(
                "Pub/sub outbox full for {ConnectionId}; disconnecting the slow subscriber",
                ConnectionId);
            _connection.ShouldClose = true;
            _connection.RequestClose();
            _outbox.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _outbox.Writer.TryComplete();
        try
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _lifetime.Dispose();
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await _connection.PushMessageAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Expected during disconnect.
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Pub/sub pump stopped for {ConnectionId}", ConnectionId);
            _connection.ShouldClose = true;
            _connection.RequestClose();
        }
    }
}
