using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Chaos;
using NovaDB.Configuration;
using NovaDB.Core.Sessions;
using NovaDB.Protocol;

namespace NovaDB.Networking;

/// <summary>
/// High-performance RESP client connection backed by <see cref="System.IO.Pipelines"/> over a TCP socket.
/// </summary>
public sealed class ClientConnection : IClientConnection, IAsyncDisposable
{
    private static long s_nextConnectionId;

    private readonly Socket _socket;
    private readonly ICommandProcessor _commandProcessor;
    private readonly ILogger<ClientConnection> _logger;
    private readonly NovaDbOptions _options;
    private readonly TlsCertificateProvider? _tls;
    private readonly IChaosFaultEngine? _chaos;
    private readonly TimeSpan _idleTimeout;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _connectionLifetime = new();
    private CancellationTokenRegistration _shutdownRegistration;

    private PipeReader? _reader;
    private PipeWriter? _writer;
    private Task? _inputPump;
    private Task? _outputPump;
    private Stream? _transport;
    private long _lastActivityTimestamp;
    private int _disposed;
    private volatile bool _outputClosed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientConnection"/> class.
    /// </summary>
    public ClientConnection(
        Socket socket,
        ICommandProcessor commandProcessor,
        IOptions<NovaDbOptions> options,
        ILogger<ClientConnection> logger,
        CancellationToken shutdownToken,
        TlsCertificateProvider? tls = null,
        IChaosFaultEngine? chaos = null)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(commandProcessor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _socket = socket;
        _commandProcessor = commandProcessor;
        _logger = logger;
        _options = options.Value;
        _tls = tls;
        _chaos = chaos;
        _idleTimeout = _options.IdleTimeout;

        ConnectionId = Interlocked.Increment(ref s_nextConnectionId).ToString(CultureInfo.InvariantCulture);
        Session = new ClientSession(ConnectionId);
        RemoteAddress = (_socket.RemoteEndPoint as System.Net.IPEndPoint)?.Address.ToString() ?? string.Empty;

        _socket.NoDelay = true;
        try
        {
            _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (SocketException)
        {
            // Platform may not support; idle timeout remains the fallback.
        }
        if (shutdownToken.CanBeCanceled)
        {
            _shutdownRegistration = shutdownToken.Register(
                static state =>
                {
                    var cts = (CancellationTokenSource)state!;
                    try
                    {
                        if (!cts.IsCancellationRequested)
                        {
                            cts.Cancel();
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // Connection already disposed.
                    }
                },
                _connectionLifetime);
        }

        TouchActivity();
    }

    /// <inheritdoc />
    public string ConnectionId { get; }

    /// <inheritdoc />
    public string RemoteAddress { get; }

    /// <inheritdoc />
    public bool IsAuthenticated
    {
        get => Session.IsAuthenticated;
        set => Session.IsAuthenticated = value;
    }

    /// <inheritdoc />
    public bool IsSubscribed { get; set; }

    /// <inheritdoc />
    public bool ShouldClose { get; set; }

    /// <inheritdoc />
    public ClientSession Session { get; }

    /// <inheritdoc />
    public void RequestClose()
    {
        ShouldClose = true;
        try
        {
            if (!_connectionLifetime.IsCancellationRequested)
            {
                _connectionLifetime.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Runs the read/process/write loop until the connection closes or is cancelled.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _connectionLifetime.Token);

        var token = linkedCts.Token;

        _logger.LogDebug(
            "Client connected {ConnectionId} from {RemoteEndPoint}",
            ConnectionId,
            _socket.RemoteEndPoint);

        try
        {
            await OpenTransportAsync(token).ConfigureAwait(false);
            var reader = _reader!;
            var writer = _writer!;

            while (!token.IsCancellationRequested && !_outputClosed)
            {
                if (IsIdleTimedOut())
                {
                    _logger.LogDebug(
                        "Client {ConnectionId} idle timeout after {IdleTimeout}",
                        ConnectionId,
                        _idleTimeout);
                    break;
                }

                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                ApplyIdleTimeout(readCts);

                ReadResult readResult;
                try
                {
                    if (_chaos is not null)
                    {
                        await _chaos.BeforeNetworkReadAsync(readCts.Token).ConfigureAwait(false);
                    }

                    readResult = await reader.ReadAsync(readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    if (IsIdleTimedOut())
                    {
                        _logger.LogDebug(
                            "Client {ConnectionId} idle timeout after {IdleTimeout}",
                            ConnectionId,
                            _idleTimeout);
                    }

                    break;
                }

                var buffer = readResult.Buffer;

                while (!_outputClosed && RespParser.TryParse(ref buffer, out var request, out var consumed))
                {
                    buffer = buffer.Slice(consumed);
                    TouchActivity();

                    if (request is null)
                    {
                        continue;
                    }

                    var response = await _commandProcessor
                        .ProcessAsync(this, request, token)
                        .ConfigureAwait(false);

                    await WriteResponseAsync(response, token).ConfigureAwait(false);

                    if (ShouldClose)
                    {
                        _logger.LogDebug("Client {ConnectionId} closing after QUIT", ConnectionId);
                        break;
                    }
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (readResult.IsCompleted || ShouldClose)
                {
                    break;
                }
            }
        }
        catch (AuthenticationException ex)
        {
            _logger.LogWarning(ex, "TLS handshake failed for client {ConnectionId}", ConnectionId);
        }
        catch (ProtocolException ex)
        {
            _logger.LogWarning(ex, "Protocol error on client {ConnectionId}", ConnectionId);
            await TryWriteErrorAsync(ex.Message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _logger.LogDebug("Client {ConnectionId} cancelled during shutdown", ConnectionId);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            _logger.LogDebug(ex, "Client {ConnectionId} disconnected due to I/O error", ConnectionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error on client {ConnectionId}", ConnectionId);
        }
        finally
        {
            _logger.LogDebug("Client disconnected {ConnectionId}", ConnectionId);
            try
            {
                await _commandProcessor
                    .OnDisconnectedAsync(ConnectionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error cleaning up command state for {ConnectionId}", ConnectionId);
            }

            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask PushMessageAsync(RespValue message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await WriteResponseAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdownRegistration.Dispose();

        try
        {
            _connectionLifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        await CompleteWriterAsync().ConfigureAwait(false);
        await CompleteReaderAsync().ConfigureAwait(false);

        try
        {
            if (_inputPump is not null && _outputPump is not null)
            {
                await Task.WhenAll(_inputPump, _outputPump).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Socket pipeline pump faulted for client {ConnectionId}", ConnectionId);
        }

        if (_transport is not null)
        {
            try
            {
                await _transport.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing transport for client {ConnectionId}", ConnectionId);
            }
        }

        try
        {
            _socket.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing socket for client {ConnectionId}", ConnectionId);
        }

        _writeLock.Dispose();
        _connectionLifetime.Dispose();
    }

    private async Task OpenTransportAsync(CancellationToken cancellationToken)
    {
        Stream transport = new NetworkStream(_socket, ownsSocket: false);

        if (_tls is { IsEnabled: true })
        {
            var certificate = _tls.GetCertificate();
            var ssl = new SslStream(transport, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsServerAsync(
                new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck
                },
                cancellationToken).ConfigureAwait(false);

            transport = ssl;
            _logger.LogDebug("TLS handshake completed for {ConnectionId}", ConnectionId);
        }

        _transport = transport;
        var pipeline = SocketPipeline.Create(transport, _connectionLifetime.Token);
        _reader = pipeline.Reader;
        _writer = pipeline.Writer;
        _inputPump = pipeline.InputPump;
        _outputPump = pipeline.OutputPump;
    }

    private async ValueTask WriteResponseAsync(RespValue response, CancellationToken cancellationToken)
    {
        if (_writer is null)
        {
            throw new InvalidOperationException("Transport is not open.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_chaos is not null)
            {
                await _chaos.BeforeNetworkWriteAsync(cancellationToken).ConfigureAwait(false);
            }

            RespWriter.Write(_writer, response);
            var flushResult = await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            // IsCompleted means the output pump stopped reading, i.e. the peer is gone. It is a
            // terminal condition, never a signal to flush again.
            if (flushResult.IsCompleted || flushResult.IsCanceled)
            {
                _outputClosed = true;
            }

            TouchActivity();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async ValueTask TryWriteErrorAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await WriteResponseAsync(RespValue.Error($"ERR {message}"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to write protocol error to client {ConnectionId}", ConnectionId);
        }
    }

    private void TouchActivity()
    {
        _lastActivityTimestamp = Stopwatch.GetTimestamp();
    }

    private bool IsIdleTimedOut()
    {
        if (_idleTimeout <= TimeSpan.Zero)
        {
            return false;
        }

        return Stopwatch.GetElapsedTime(_lastActivityTimestamp) >= _idleTimeout;
    }

    private void ApplyIdleTimeout(CancellationTokenSource readCts)
    {
        if (_idleTimeout <= TimeSpan.Zero)
        {
            return;
        }

        var remaining = _idleTimeout - Stopwatch.GetElapsedTime(_lastActivityTimestamp);
        if (remaining <= TimeSpan.Zero)
        {
            readCts.Cancel();
            return;
        }

        readCts.CancelAfter(remaining);
    }

    private async Task CompleteWriterAsync()
    {
        if (_writer is null)
        {
            return;
        }

        try
        {
            await _writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error completing writer for client {ConnectionId}", ConnectionId);
        }
    }

    private async Task CompleteReaderAsync()
    {
        if (_reader is null)
        {
            return;
        }

        try
        {
            await _reader.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error completing reader for client {ConnectionId}", ConnectionId);
        }
    }
}
