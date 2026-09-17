using NovaDB.Core.Models;
using NovaDB.Storage.Values;

namespace NovaDB.Persistence.Snapshot;

/// <summary>
/// Deep-copies database entries so a snapshot can serialize without holding the exclusive gate.
/// </summary>
internal static class SnapshotEntryCloner
{
    public static (RedisKey Key, DatabaseEntry Entry) Clone(RedisKey key, DatabaseEntry entry)
    {
        var keyCopy = new RedisKey((byte[])key.Bytes.Clone());
        var valueCopy = CloneValue(entry.Type, entry.Value);
        var clone = new DatabaseEntry(entry.Type, valueCopy, entry.ExpireAtUnixMs)
        {
            Version = entry.Version,
            LastAccessTicks = entry.LastAccessTicks,
            AccessFrequency = entry.AccessFrequency
        };
        return (keyCopy, clone);
    }

    private static object CloneValue(RedisValueType type, object value)
        => type switch
        {
            RedisValueType.String => CloneString((StringValue)value),
            RedisValueType.Hash => CloneHash((HashValue)value),
            RedisValueType.List => CloneList((ListValue)value),
            RedisValueType.Set => CloneSet((SetValue)value),
            RedisValueType.SortedSet => CloneSortedSet((SortedSetValue)value),
            _ => throw new InvalidOperationException($"Unsupported value type: {type}")
        };

    private static StringValue CloneString(StringValue source)
        => new((byte[])source.Bytes.Clone());

    private static HashValue CloneHash(HashValue source)
    {
        var clone = new HashValue();
        var flat = source.GetAllFlat();
        for (var i = 0; i < flat.Length; i += 2)
        {
            clone.SetField((byte[])flat[i].Clone(), (byte[])flat[i + 1].Clone());
        }

        return clone;
    }

    private static ListValue CloneList(ListValue source)
    {
        var clone = new ListValue();
        foreach (var element in source.ToArray())
        {
            clone.PushTail((byte[])element.Clone());
        }

        return clone;
    }

    private static SetValue CloneSet(SetValue source)
    {
        var clone = new SetValue();
        foreach (var member in source.GetMembers())
        {
            clone.Add((byte[])member.Clone());
        }

        return clone;
    }

    private static SortedSetValue CloneSortedSet(SortedSetValue source)
    {
        var clone = new SortedSetValue();
        foreach (var (score, member) in source.RangeByRankWithScores(0, -1))
        {
            clone.Add(score, (byte[])member.Clone());
        }

        return clone;
    }
}
