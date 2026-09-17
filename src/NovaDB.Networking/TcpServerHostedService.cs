using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Protocol;

namespace NovaDB.Networking;

/// <summary>
/// Background service that accepts TCP connections and serves the Redis RESP protocol.
/// </summary>
public sealed class TcpServerHostedService : BackgroundService
{
    private const int ListenBacklog = 512;
    private static readonly TimeSpan ShutdownDrainTimeout = TimeSpan.FromSeconds(30);
    private static readonly byte[] MaxClientsError = Encoding.UTF8.GetBytes("-ERR max number of clients reached\r\n");

    private readonly IOptions<NovaDbOptions> _optionsAccessor;
    private readonly NovaDbOptions _options;
    private readonly ConnectionManager _connectionManager;
    private readonly ICommandProcessor _commandProcessor;
    private readonly ILogger<TcpServerHostedService> _logger;
    private readonly ILogger<ClientConnection> _connectionLogger;
    private readonly TlsCertificateProvider _tls;
    private readonly Func<CancellationToken, Task>? _waitUntilReady;
    private readonly ConcurrentDictionary<Guid, Task> _connectionTasks = new();

    private Socket? _listener;

    /// <summary>
    /// Initializes a new instance of the <see cref="TcpServerHostedService"/> class.
    /// </summary>
    public TcpServerHostedService(
        IOptions<NovaDbOptions> options,
        ConnectionManager connectionManager,
        ICommandProcessor commandProcessor,
        ILogger<TcpServerHostedService> logger,
        ILoggerFactory loggerFactory,
        TlsCertificateProvider tls)
        : this(options, connectionManager, commandProcessor, logger, loggerFactory, tls, waitUntilReady: null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TcpServerHostedService"/> class with a readiness gate.
    /// </summary>
    public TcpServerHostedService(
        IOptions<NovaDbOptions> options,
        ConnectionManager connectionManager,
        ICommandProcessor commandProcessor,
        ILogger<TcpServerHostedService> logger,
        ILoggerFactory loggerFactory,
        TlsCertificateProvider tls,
        Func<CancellationToken, Task>? waitUntilReady)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connectionManager);
        ArgumentNullException.ThrowIfNull(commandProcessor);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(tls);

        _optionsAccessor = options;
        _options = options.Value;
        _connectionManager = connectionManager;
        _commandProcessor = commandProcessor;
        _logger = logger;
        _connectionLogger = loggerFactory.CreateLogger<ClientConnection>();
        _tls = tls;
        _waitUntilReady = waitUntilReady;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping TCP server on {BindAddress}:{Port}", _options.BindAddress, _options.Port);

        if (_listener is not null)
        {
            try
            {
                _listener.Close();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error closing TCP listener");
            }
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        var active = _connectionTasks.Values.ToArray();
        if (active.Length > 0)
        {
            await Task.WhenAll(active).WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _connectionManager
                .WaitForDrainAsync(ShutdownDrainTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Shutdown drain timed out with {ActiveCount} active connections",
                _connectionManager.ActiveCount);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_waitUntilReady is not null)
        {
            _logger.LogInformation("Waiting for database recovery before accepting connections");
            await _waitUntilReady(stoppingToken).ConfigureAwait(false);
        }

        var bindAddress = ResolveBindAddress(_options.BindAddress);
        var endpoint = new IPEndPoint(bindAddress, _options.Port);
        _listener = CreateListener(bindAddress.AddressFamily);

        _listener.Bind(endpoint);
        _listener.Listen(ListenBacklog);

        _logger.LogInformation(
            "NovaDB TCP server listening on {BindAddress}:{Port} (max connections: {MaxConnections})",
            endpoint.Address,
            endpoint.Port,
            _options.MaxConnections);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                Socket acceptedSocket;
                try
                {
                    acceptedSocket = await _listener
                        .AcceptAsync(stoppingToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                if (!_connectionManager.TryAcquire())
                {
                    // Do not block the accept loop on a slow rejected peer.
                    _ = RejectConnectionAsync(acceptedSocket, CancellationToken.None);
                    continue;
                }

                var connection = new ClientConnection(
                    acceptedSocket,
                    _commandProcessor,
                    _optionsAccessor,
                    _connectionLogger,
                    stoppingToken,
                    _tls);

                var id = Guid.NewGuid();
                var connectionTask = HandleConnectionAsync(connection, id, stoppingToken);
                _connectionTasks[id] = connectionTask;
            }
        }
        finally
        {
            _listener?.Dispose();
            _listener = null;
        }
    }

    private async Task HandleConnectionAsync(ClientConnection connection, Guid id, CancellationToken stoppingToken)
    {
        _connectionManager.Track(connection);
        try
        {
            await connection.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in connection {ConnectionId}", connection.ConnectionId);
        }
        finally
        {
            _connectionManager.Untrack(connection.ConnectionId);
            _connectionTasks.TryRemove(id, out _);
            _connectionManager.Release();
        }
    }

    private static async Task RejectConnectionAsync(Socket socket, CancellationToken cancellationToken)
    {
        try
        {
            await socket.SendAsync(MaxClientsError, SocketFlags.None, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Best effort while rejecting excess clients.
        }
        finally
        {
            socket.Dispose();
        }
    }

    private static Socket CreateListener(AddressFamily addressFamily)
    {
        var listener = new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        return listener;
    }

    private static IPAddress ResolveBindAddress(string bindAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindAddress);

        if (bindAddress is "*" or "0.0.0.0")
        {
            return IPAddress.Any;
        }

        if (bindAddress is "::" or "[::]")
        {
            return IPAddress.IPv6Any;
        }

        if (IPAddress.TryParse(bindAddress, out var address))
        {
            return address;
        }

        throw new InvalidOperationException($"Invalid bind address: {bindAddress}");
    }
}
