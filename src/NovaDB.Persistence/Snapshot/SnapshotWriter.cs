using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Core.Models;
using NovaDB.Persistence.Internal;
using NovaDB.Storage;

namespace NovaDB.Persistence.Snapshot;

/// <summary>
/// Writes NovaDB binary snapshots from a storage engine scan.
/// </summary>
public sealed class SnapshotWriter
{
    private readonly NovaDbOptions _options;
    private readonly ILogger<SnapshotWriter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotWriter"/> class.
    /// </summary>
    /// <param name="options">Database options.</param>
    /// <param name="logger">Logger instance.</param>
    public SnapshotWriter(IOptions<NovaDbOptions> options, ILogger<SnapshotWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Writes a snapshot file atomically from the supplied storage engine.
    /// </summary>
    /// <param name="storage">Storage engine to scan.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WriteAsync(IStorageEngine storage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);

        Directory.CreateDirectory(_options.DataDirectory);
        var tempPath = Path.Combine(_options.DataDirectory, SnapshotFormat.SnapshotTempFileName);
        var finalPath = SnapshotFormat.GetSnapshotPath(_options.DataDirectory, 0);

        await using (var tempStream = new FileStream(
                         tempPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 64 * 1024,
                         options: FileOptions.SequentialScan))
        {
            await WriteSnapshotContentAsync(storage, tempStream, cancellationToken).ConfigureAwait(false);
        }

        RotateGenerations(finalPath);
        File.Move(tempPath, finalPath);

        _logger.LogInformation("Snapshot written to {SnapshotPath}", finalPath);
    }

    /// <summary>
    /// Shifts existing snapshots so the previous live file becomes generation 1, etc.
    /// </summary>
    private void RotateGenerations(string finalPath)
    {
        var keep = Math.Max(1, _options.SnapshotGenerationCount);
        // Drop the oldest generation we will no longer retain.
        var oldest = SnapshotFormat.GetSnapshotPath(_options.DataDirectory, keep);
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var generation = keep - 1; generation >= 1; generation--)
        {
            var source = SnapshotFormat.GetSnapshotPath(_options.DataDirectory, generation - 1);
            var destination = SnapshotFormat.GetSnapshotPath(_options.DataDirectory, generation);
            if (File.Exists(source))
            {
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                File.Move(source, destination);
            }
        }
    }

    private async Task WriteSnapshotContentAsync(
        IStorageEngine storage,
        Stream output,
        CancellationToken cancellationToken)
    {
        // Short exclusive section: deep-clone the keyspace, then release so clients proceed while
        // we serialize/compress/write the immutable copy.
        var frozen = await storage.RunExclusiveAsync(
            async ct =>
            {
                var entries = new List<(RedisKey Key, DatabaseEntry Entry)>();
                await foreach (var item in storage.ScanAsync(ct).ConfigureAwait(false))
                {
                    entries.Add(SnapshotEntryCloner.Clone(item.Key, item.Entry));
                }

                return entries;
            },
            cancellationToken).ConfigureAwait(false);

        await using var bodyStream = new MemoryStream();
        Span<byte> countBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(countBytes, frozen.Count);
        bodyStream.Write(countBytes);

        foreach (var (key, entry) in frozen)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteEntry(bodyStream, key, entry);
        }

        var bodyBytes = bodyStream.ToArray();

        if (_options.SnapshotCompression)
        {
            bodyBytes = Compress(bodyBytes);
        }

        var flags = _options.SnapshotCompression ? SnapshotFormat.FlagCompressed : (ushort)0;
        var checksumPayload = new byte[2 + 2 + bodyBytes.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(checksumPayload.AsSpan(0, 2), SnapshotFormat.CurrentVersion);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(checksumPayload.AsSpan(2, 2), flags);
        bodyBytes.CopyTo(checksumPayload.AsSpan(4));

        var crc = Crc32.Compute(checksumPayload);

        output.Write(SnapshotFormat.Magic);
        output.WriteByte((byte)(SnapshotFormat.CurrentVersion & 0xFF));
        output.WriteByte((byte)((SnapshotFormat.CurrentVersion >> 8) & 0xFF));
        output.WriteByte((byte)(flags & 0xFF));
        output.WriteByte((byte)((flags >> 8) & 0xFF));
        output.Write(bodyBytes);

        Span<byte> crcBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, crc);
        output.Write(crcBytes);
    }

    private static void WriteEntry(Stream stream, RedisKey key, DatabaseEntry entry)
    {
        WriteInt32(stream, key.Bytes.Length);
        stream.Write(key.Bytes);
        stream.WriteByte((byte)entry.Type);

        var expireAt = entry.ExpireAtUnixMs ?? -1L;
        WriteInt64(stream, expireAt);
        WriteInt64(stream, entry.Version);
        SnapshotPayloadCodec.WritePayload(stream, entry);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static byte[] Compress(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(input);
        }

        return output.ToArray();
    }
}
