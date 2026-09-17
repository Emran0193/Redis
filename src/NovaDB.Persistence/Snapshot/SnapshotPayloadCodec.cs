using System.Text;
using NovaDB.Core.Models;
using NovaDB.Storage.Values;

namespace NovaDB.Persistence.Snapshot;

/// <summary>
/// Serializes and deserializes type-specific value payloads for snapshots.
/// </summary>
internal static class SnapshotPayloadCodec
{
    /// <summary>
    /// Writes the payload for a database entry to the output stream.
    /// </summary>
    /// <param name="stream">Destination stream.</param>
    /// <param name="entry">Entry whose value should be serialized.</param>
    public static void WritePayload(Stream stream, DatabaseEntry entry)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entry);

        switch (entry.Type)
        {
            case RedisValueType.String:
                WriteBytes(stream, GetStringBytes(entry));
                break;
            case RedisValueType.Hash:
                WriteHash(stream, GetHashValue(entry));
                break;
            case RedisValueType.List:
                WriteList(stream, GetListValue(entry));
                break;
            case RedisValueType.Set:
                WriteSet(stream, GetSetValue(entry));
                break;
            case RedisValueType.SortedSet:
                WriteSortedSet(stream, GetSortedSetValue(entry));
                break;
            default:
                throw new InvalidOperationException($"Unsupported value type: {entry.Type}");
        }
    }

    /// <summary>
    /// Reads a payload and reconstructs a typed value object.
    /// </summary>
    /// <param name="reader">Binary reader positioned at the payload.</param>
    /// <param name="type">Expected value type.</param>
    /// <returns>Reconstructed value object.</returns>
    public static object ReadPayload(BinaryReader reader, RedisValueType type)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return type switch
        {
            RedisValueType.String => new StringValue(ReadBytes(reader)),
            RedisValueType.Hash => ReadHash(reader),
            RedisValueType.List => ReadList(reader),
            RedisValueType.Set => ReadSet(reader),
            RedisValueType.SortedSet => ReadSortedSet(reader),
            _ => throw new InvalidOperationException($"Unsupported value type: {type}")
        };
    }

    private static void WriteHash(Stream stream, HashValue hash)
    {
        var flat = hash.GetAllFlat();
        WriteInt32(stream, flat.Length / 2);
        for (var i = 0; i < flat.Length; i += 2)
        {
            WriteBytes(stream, flat[i]);
            WriteBytes(stream, flat[i + 1]);
        }
    }

    private static HashValue ReadHash(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException("Invalid hash field count.");
        }

        var hash = new HashValue();
        for (var i = 0; i < count; i++)
        {
            var fieldBytes = ReadBytes(reader);
            var valueBytes = ReadBytes(reader);
            hash.SetField(fieldBytes, valueBytes);
        }

        return hash;
    }

    private static void WriteList(Stream stream, ListValue list)
    {
        var elements = list.ToArray();
        WriteInt32(stream, elements.Length);
        foreach (var element in elements)
        {
            WriteBytes(stream, element);
        }
    }

    private static ListValue ReadList(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException("Invalid list element count.");
        }

        var list = new ListValue();
        for (var i = 0; i < count; i++)
        {
            list.PushTail(ReadBytes(reader));
        }

        return list;
    }

    private static void WriteSet(Stream stream, SetValue set)
    {
        var members = set.GetMembers();
        WriteInt32(stream, members.Length);
        foreach (var member in members)
        {
            WriteBytes(stream, member);
        }
    }

    private static SetValue ReadSet(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException("Invalid set member count.");
        }

        var set = new SetValue();
        for (var i = 0; i < count; i++)
        {
            set.Add(ReadBytes(reader));
        }

        return set;
    }

    private static void WriteSortedSet(Stream stream, SortedSetValue sortedSet)
    {
        if (sortedSet.Count == 0)
        {
            WriteInt32(stream, 0);
            return;
        }

        var entries = sortedSet.RangeByRankWithScores(0, sortedSet.Count - 1);
        WriteInt32(stream, entries.Length);
        foreach (var (score, member) in entries)
        {
            WriteDouble(stream, score);
            WriteBytes(stream, member);
        }
    }

    private static SortedSetValue ReadSortedSet(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException("Invalid sorted set member count.");
        }

        var sortedSet = new SortedSetValue();
        for (var i = 0; i < count; i++)
        {
            var score = ReadDouble(reader);
            var member = ReadBytes(reader);
            sortedSet.Add(score, member);
        }

        return sortedSet;
    }

    private static byte[] GetStringBytes(DatabaseEntry entry)
    {
        return entry.Value switch
        {
            StringValue stringValue => stringValue.Bytes,
            byte[] bytes => bytes,
            _ => throw new InvalidOperationException("String entry value has an unexpected runtime type.")
        };
    }

    private static HashValue GetHashValue(DatabaseEntry entry)
    {
        return entry.Value as HashValue
            ?? throw new InvalidOperationException("Hash entry value has an unexpected runtime type.");
    }

    private static ListValue GetListValue(DatabaseEntry entry)
    {
        return entry.Value as ListValue
            ?? throw new InvalidOperationException("List entry value has an unexpected runtime type.");
    }

    private static SetValue GetSetValue(DatabaseEntry entry)
    {
        return entry.Value as SetValue
            ?? throw new InvalidOperationException("Set entry value has an unexpected runtime type.");
    }

    private static SortedSetValue GetSortedSetValue(DatabaseEntry entry)
    {
        return entry.Value as SortedSetValue
            ?? throw new InvalidOperationException("Sorted set entry value has an unexpected runtime type.");
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteDouble(Stream stream, double value)
    {
        Span<byte> buffer = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteBytes(Stream stream, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static byte[] ReadBytes(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0)
        {
            throw new InvalidDataException("Invalid byte array length.");
        }

        return reader.ReadBytes(length);
    }

    private static double ReadDouble(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(8);
        if (bytes.Length != 8)
        {
            throw new EndOfStreamException("Unexpected end of stream while reading double.");
        }

        return System.Buffers.Binary.BinaryPrimitives.ReadDoubleLittleEndian(bytes);
    }
}
