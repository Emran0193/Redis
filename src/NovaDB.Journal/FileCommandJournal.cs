using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;

namespace NovaDB.Journal;

/// <summary>
/// File-backed append-only command journal with CRC32 validation and streaming reads.
/// </summary>
public sealed class FileCommandJournal : ICommandJournal, IHostedService, IAsyncDisposable
{
    private static readonly byte[] Magic = [(byte)'N', (byte)'D', (byte)'B', (byte)'J'];
    private const ushort FormatVersion = 1;

    private readonly IOptions<NovaDbOptions> _options;
    private readonly ILogger<FileCommandJournal> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _stateLock = new();

    private FileStream? _stream;
    private ulong _nextEventId = 1;
    private ulong _currentOffset;
    private string? _lastDedupeKey;
    private string _filePath = string.Empty;
    private bool _started;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileCommandJournal"/> class.
    /// </summary>
    public FileCommandJournal(IOptions<NovaDbOptions> options, ILogger<FileCommandJournal> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string FilePath => _filePath;

    /// <inheritdoc />
    public ulong CurrentOffset => Volatile.Read(ref _currentOffset);

    /// <inheritdoc />
    public long SizeBytes
    {
        get
        {
            lock (_stateLock)
            {
                return _stream?.Length ?? 0;
            }
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.JournalEnabled)
        {
            _logger.LogInformation("Command journal disabled by configuration");
            return;
        }

        var directory = Path.GetFullPath(_options.Value.DataDirectory);
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "commands.ndbj");

        _stream = new FileStream(
            _filePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (_stream.Length == 0)
        {
            await WriteHeaderAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ValidateHeaderAsync(cancellationToken).ConfigureAwait(false);
            _currentOffset = await ScanLastEventIdAsync(cancellationToken).ConfigureAwait(false);
            _nextEventId = _currentOffset + 1;
            _stream.Seek(0, SeekOrigin.End);
        }

        _started = true;
        _logger.LogInformation(
            "Command journal ready at {Path} (offset {Offset})",
            _filePath,
            _currentOffset);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
            _started = false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<CommandJournalEvent> AppendAsync(
        string clientId,
        string transactionId,
        string command,
        ReadOnlyMemory<byte> key,
        IReadOnlyList<ReadOnlyMemory<byte>> arguments,
        ulong version,
        string? dedupeKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        arguments ??= Array.Empty<ReadOnlyMemory<byte>>();

        if (!_started || _stream is null)
        {
            throw new InvalidOperationException("Command journal is not started.");
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (dedupeKey is not null
                && _lastDedupeKey is not null
                && string.Equals(dedupeKey, _lastDedupeKey, StringComparison.Ordinal))
            {
                // Idempotent retry: return a synthetic view of the last offset without rewriting.
                return new CommandJournalEvent(
                    _currentOffset,
                    DateTimeOffset.UtcNow,
                    clientId ?? string.Empty,
                    transactionId ?? string.Empty,
                    command,
                    arguments,
                    key,
                    version,
                    Checksum: 0);
            }

            var eventId = _nextEventId;
            var timestamp = DateTimeOffset.UtcNow;
            var payload = EncodePayload(
                eventId,
                timestamp,
                clientId ?? string.Empty,
                transactionId ?? string.Empty,
                command,
                key,
                arguments,
                version);
            var checksum = Crc32.HashToUInt32(payload);

            // Frame: uint32 length | payload | uint32 crc
            var length = payload.Length;
            var frameHeader = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(frameHeader, (uint)length);
            await _stream.WriteAsync(frameHeader, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);

            var crcBytes = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, checksum);
            await _stream.WriteAsync(crcBytes, cancellationToken).ConfigureAwait(false);

            if (_options.Value.JournalFlushOnAppend)
            {
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            _nextEventId = eventId + 1;
            Volatile.Write(ref _currentOffset, eventId);
            _lastDedupeKey = dedupeKey;

            return new CommandJournalEvent(
                eventId,
                timestamp,
                clientId ?? string.Empty,
                transactionId ?? string.Empty,
                command,
                arguments,
                key,
                version,
                checksum);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CommandJournalEvent> ReadAsync(
        ulong fromExclusiveOffset,
        string? keyFilter,
        string? commandFilter,
        int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (pageSize <= 0)
        {
            pageSize = 100;
        }

        if (!_started || string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
        {
            yield break;
        }

        await using var read = new FileStream(
            _filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (read.Length < 8)
        {
            yield break;
        }

        read.Seek(8, SeekOrigin.Begin);
        var emitted = 0;
        byte[]? keyFilterBytes = keyFilter is null ? null : Encoding.UTF8.GetBytes(keyFilter);

        while (emitted < pageSize && read.Position < read.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryReadFrame(read, out var evt))
            {
                yield break;
            }

            if (evt.EventId <= fromExclusiveOffset)
            {
                continue;
            }

            if (commandFilter is not null
                && !string.Equals(evt.Command, commandFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (keyFilterBytes is not null
                && !evt.Key.Span.SequenceEqual(keyFilterBytes))
            {
                continue;
            }

            emitted++;
            yield return evt;
        }
    }

    /// <inheritdoc />
    public async Task ReplayAsync(
        Func<CommandJournalEvent, CancellationToken, ValueTask> onEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onEvent);

        await foreach (var evt in ReadAsync(0, null, null, int.MaxValue, cancellationToken).ConfigureAwait(false))
        {
            await onEvent(evt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private async Task WriteHeaderAsync(CancellationToken cancellationToken)
    {
        var header = new byte[8];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), FormatVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), 0);
        await _stream!.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateHeaderAsync(CancellationToken cancellationToken)
    {
        var header = new byte[8];
        _stream!.Seek(0, SeekOrigin.Begin);
        var read = await _stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        if (read != 8
            || !header.AsSpan(0, 4).SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != FormatVersion)
        {
            throw new InvalidDataException($"Invalid command journal header at {_filePath}");
        }
    }

    private async Task<ulong> ScanLastEventIdAsync(CancellationToken cancellationToken)
    {
        ulong last = 0;
        _stream!.Seek(8, SeekOrigin.Begin);
        while (_stream.Position < _stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryReadFrame(_stream, out var evt))
            {
                _logger.LogWarning(
                    "Truncating corrupt journal tail at position {Position}",
                    _stream.Position);
                _stream.SetLength(_stream.Position);
                break;
            }

            last = evt.EventId;
        }

        return last;
    }

    private static bool TryReadFrame(Stream stream, out CommandJournalEvent evt)
    {
        evt = null!;
        Span<byte> lenBuf = stackalloc byte[4];
        if (!ReadExact(stream, lenBuf))
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(lenBuf);
        if (length == 0 || length > 64 * 1024 * 1024)
        {
            return false;
        }

        var payload = new byte[length];
        if (!ReadExact(stream, payload))
        {
            return false;
        }

        Span<byte> crcBuf = stackalloc byte[4];
        if (!ReadExact(stream, crcBuf))
        {
            return false;
        }

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(crcBuf);
        var actual = Crc32.HashToUInt32(payload);
        if (expected != actual)
        {
            // Rewind to start of corrupt frame so caller can truncate.
            stream.Seek(-(4 + length + 4), SeekOrigin.Current);
            return false;
        }

        evt = DecodePayload(payload, expected);
        return true;
    }

    private static bool ReadExact(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    private static byte[] EncodePayload(
        ulong eventId,
        DateTimeOffset timestamp,
        string clientId,
        string transactionId,
        string command,
        ReadOnlyMemory<byte> key,
        IReadOnlyList<ReadOnlyMemory<byte>> arguments,
        ulong version)
    {
        var clientBytes = Encoding.UTF8.GetBytes(clientId);
        var txBytes = Encoding.UTF8.GetBytes(transactionId);
        var cmdBytes = Encoding.UTF8.GetBytes(command);

        var size = 8 + 8 + 2 + clientBytes.Length + 2 + txBytes.Length + 2 + cmdBytes.Length
                   + 2 + key.Length + 2 + 8;
        for (var i = 0; i < arguments.Count; i++)
        {
            size += 4 + arguments[i].Length;
        }

        var buffer = new byte[size];
        var offset = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), eventId);
        offset += 8;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset), timestamp.UtcTicks);
        offset += 8;
        offset = WritePrefixed(buffer, offset, clientBytes);
        offset = WritePrefixed(buffer, offset, txBytes);
        offset = WritePrefixed(buffer, offset, cmdBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)key.Length);
        offset += 2;
        key.Span.CopyTo(buffer.AsSpan(offset));
        offset += key.Length;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)arguments.Count);
        offset += 2;
        for (var i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), (uint)arg.Length);
            offset += 4;
            arg.Span.CopyTo(buffer.AsSpan(offset));
            offset += arg.Length;
        }

        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), version);
        return buffer;
    }

    private static int WritePrefixed(byte[] buffer, int offset, byte[] value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset), (ushort)value.Length);
        offset += 2;
        value.CopyTo(buffer, offset);
        return offset + value.Length;
    }

    private static CommandJournalEvent DecodePayload(byte[] payload, uint checksum)
    {
        var offset = 0;
        var eventId = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(offset));
        offset += 8;
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(offset));
        offset += 8;
        offset = ReadPrefixed(payload, offset, out var client);
        offset = ReadPrefixed(payload, offset, out var tx);
        offset = ReadPrefixed(payload, offset, out var command);
        var keyLen = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset));
        offset += 2;
        var key = payload.AsMemory(offset, keyLen);
        offset += keyLen;
        var argCount = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset));
        offset += 2;
        var args = new ReadOnlyMemory<byte>[argCount];
        for (var i = 0; i < argCount; i++)
        {
            var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));
            offset += 4;
            args[i] = payload.AsMemory(offset, len);
            offset += len;
        }

        var version = BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(offset));

        return new CommandJournalEvent(
            eventId,
            new DateTimeOffset(ticks, TimeSpan.Zero),
            Encoding.UTF8.GetString(client),
            Encoding.UTF8.GetString(tx),
            Encoding.UTF8.GetString(command),
            args,
            key,
            version,
            checksum);
    }

    private static int ReadPrefixed(byte[] payload, int offset, out byte[] value)
    {
        var len = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset));
        offset += 2;
        value = payload.AsSpan(offset, len).ToArray();
        return offset + len;
    }
}
