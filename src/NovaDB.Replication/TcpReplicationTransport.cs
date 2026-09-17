using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;

namespace NovaDB.Replication;

/// <summary>
/// Stub TCP replication transport. Foundation only — frames are raw length-prefixed bytes;
/// leader election and full protocol negotiation are intentionally out of scope.
/// </summary>
public sealed class TcpReplicationTransport : IReplicationTransport
{
    private readonly IOptions<NovaDbOptions> _options;
    private readonly ILogger<TcpReplicationTransport> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;

    /// <summary>
    /// Initializes a new instance of the <see cref="TcpReplicationTransport"/> class.
    /// </summary>
    public TcpReplicationTransport(IOptions<NovaDbOptions> options, ILogger<TcpReplicationTransport> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets or sets the peer endpoint (host:port). Empty disables connect attempts.
    /// </summary>
    public string PeerEndpoint { get; set; } = string.Empty;

    /// <inheritdoc />
    public bool IsConnected => _client?.Connected == true && _stream is not null;

    /// <inheritdoc />
    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(PeerEndpoint))
        {
            _logger.LogDebug("TcpReplicationTransport peer not configured; remaining idle");
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            var parts = PeerEndpoint.Split(':', 2);
            var host = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : _options.Value.Port + 1;
            _client = new TcpClient();
            await _client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            _stream = _client.GetStream();
            _logger.LogInformation("Replication transport connected to {Peer}", PeerEndpoint);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Replication transport is not connected.");
        }

        var length = BitConverter.GetBytes(frame.Length);
        await _stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        if (!frame.IsEmpty)
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException("Replication transport is not connected.");
        }

        var lengthBuf = new byte[4];
        await ReadExactAsync(_stream, lengthBuf, cancellationToken).ConfigureAwait(false);
        var length = BitConverter.ToInt32(lengthBuf);
        if (length < 0 || length > _options.Value.MaxPayloadBytes)
        {
            throw new InvalidOperationException($"Invalid replication frame length {length}.");
        }

        if (length == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var payload = new byte[length];
        await ReadExactAsync(_stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async ValueTask DisposeCoreAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        _client?.Dispose();
        _client = null;
    }

    private static async Task ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Replication peer closed the connection.");
            }

            offset += read;
        }
    }
}
