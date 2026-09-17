using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Core.Models;
using NovaDB.Persistence.Internal;

namespace NovaDB.Persistence.Snapshot;

/// <summary>
/// Reads NovaDB binary snapshots from disk with CRC validation.
/// </summary>
public sealed class SnapshotReader
{
    private readonly NovaDbOptions _options;
    private readonly ILogger<SnapshotReader> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotReader"/> class.
    /// </summary>
    /// <param name="options">Database options.</param>
    /// <param name="logger">Logger instance.</param>
    public SnapshotReader(IOptions<NovaDbOptions> options, ILogger<SnapshotReader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reads the latest snapshot file when present.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Recovered entries, or an empty collection when no snapshot exists.</returns>
    public async Task<IReadOnlyList<(RedisKey Key, DatabaseEntry Entry)>> ReadLatestAsync(CancellationToken cancellationToken)
    {
        var keep = Math.Max(1, _options.SnapshotGenerationCount);
        InvalidDataException? lastCorruption = null;

        for (var generation = 0; generation < keep; generation++)
        {
            var snapshotPath = SnapshotFormat.GetSnapshotPath(_options.DataDirectory, generation);
            if (!File.Exists(snapshotPath))
            {
                if (generation == 0)
                {
                    _logger.LogInformation("No snapshot file found at {SnapshotPath}", snapshotPath);
                }

                continue;
            }

            try
            {
                await using var stream = new FileStream(
                    snapshotPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    options: FileOptions.SequentialScan);

                var entries = await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (generation > 0)
                {
                    _logger.LogWarning(
                        "Loaded fallback snapshot generation {Generation} from {SnapshotPath} after newer generation(s) failed validation.",
                        generation,
                        snapshotPath);
                }

                return entries;
            }
            catch (InvalidDataException ex)
            {
                lastCorruption = ex;
                _logger.LogWarning(
                    ex,
                    "Snapshot generation {Generation} at {SnapshotPath} failed validation; trying older generation.",
                    generation,
                    snapshotPath);
            }
        }

        if (lastCorruption is not null)
        {
            throw new InvalidDataException(
                "All snapshot generations failed CRC or format validation.",
                lastCorruption);
        }

        return [];
    }

    /// <summary>
    /// Reads snapshot entries from an open stream.
    /// </summary>
    /// <param name="stream">Snapshot stream positioned at the beginning.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Recovered entries.</returns>
    public async Task<IReadOnlyList<(RedisKey Key, DatabaseEntry Entry)>> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var magic = new byte[SnapshotFormat.Magic.Length];
        if (await stream.ReadAsync(magic, cancellationToken).ConfigureAwait(false) != magic.Length
            || !magic.AsSpan().SequenceEqual(SnapshotFormat.Magic))
        {
            throw new InvalidDataException("Snapshot file has an invalid magic header.");
        }

        var versionBytes = await ReadExactAsync(stream, 2, cancellationToken).ConfigureAwait(false);
        var flagsBytes = await ReadExactAsync(stream, 2, cancellationToken).ConfigureAwait(false);

        var version = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(versionBytes);
        var flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(flagsBytes);

        if (version != SnapshotFormat.CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported snapshot version: {version}.");
        }

        await using var remainder = new MemoryStream();
        await stream.CopyToAsync(remainder, cancellationToken).ConfigureAwait(false);
        var tailBytes = remainder.ToArray();

        if (tailBytes.Length < 4)
        {
            throw new InvalidDataException("Snapshot file is truncated.");
        }

        var payloadLength = tailBytes.Length - 4;
        var payloadBytes = tailBytes.AsSpan(0, payloadLength);
        var storedCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(tailBytes.AsSpan(payloadLength, 4));

        var checksumPayload = new byte[versionBytes.Length + flagsBytes.Length + payloadLength];
        versionBytes.CopyTo(checksumPayload.AsSpan(0, versionBytes.Length));
        flagsBytes.CopyTo(checksumPayload.AsSpan(versionBytes.Length, flagsBytes.Length));
        payloadBytes.CopyTo(checksumPayload.AsSpan(versionBytes.Length + flagsBytes.Length));

        var computedCrc = Crc32.Compute(checksumPayload);
        if (computedCrc != storedCrc)
        {
            throw new InvalidDataException(
                $"Snapshot CRC mismatch. Expected {storedCrc:X8}, computed {computedCrc:X8}.");
        }

        var entriesPayload = payloadBytes.ToArray();
        if ((flags & SnapshotFormat.FlagCompressed) != 0)
        {
            entriesPayload = Decompress(entriesPayload);
        }

        return ReadEntries(entriesPayload, cancellationToken);
    }

    private static IReadOnlyList<(RedisKey Key, DatabaseEntry Entry)> ReadEntries(
        byte[] entriesPayload,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(entriesPayload, writable: false);
        using var reader = new BinaryReader(stream);

        var count = reader.ReadInt64();
        if (count < 0)
        {
            throw new InvalidDataException("Invalid snapshot entry count.");
        }

        var entries = new List<(RedisKey Key, DatabaseEntry Entry)>((int)Math.Min(count, int.MaxValue));
        for (long i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var keyLength = reader.ReadInt32();
            if (keyLength < 0)
            {
                throw new InvalidDataException("Invalid snapshot key length.");
            }

            var keyBytes = reader.ReadBytes(keyLength);
            if (keyBytes.Length != keyLength)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading snapshot key.");
            }

            var typeByte = reader.ReadByte();
            if (!Enum.IsDefined(typeof(RedisValueType), typeByte))
            {
                throw new InvalidDataException($"Unknown snapshot value type byte: {typeByte}.");
            }

            var type = (RedisValueType)typeByte;
            var expireAt = reader.ReadInt64();
            var version = reader.ReadInt64();
            var value = SnapshotPayloadCodec.ReadPayload(reader, type);

            var entry = new DatabaseEntry(type, value, expireAt < 0 ? null : expireAt)
            {
                Version = version
            };

            entries.Add((new RedisKey(keyBytes), entry));
        }

        return entries;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of stream while reading snapshot header.");
            }

            offset += read;
        }

        return buffer;
    }

    private static byte[] Decompress(byte[] input)
    {
        using var inputStream = new MemoryStream(input, writable: false);
        using var gzip = new GZipStream(inputStream, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
