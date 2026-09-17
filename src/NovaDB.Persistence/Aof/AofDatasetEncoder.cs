using System.Globalization;
using NovaDB.Core.Models;
using NovaDB.Protocol;
using NovaDB.Storage.Values;

namespace NovaDB.Persistence.Aof;

/// <summary>
/// Encodes live database entries as RESP mutating commands for AOF rewrite.
/// </summary>
internal static class AofDatasetEncoder
{
    /// <summary>
    /// Serializes one entry as the minimal RESP command(s) that recreate it on replay.
    /// </summary>
    /// <param name="key">Stored key.</param>
    /// <param name="entry">Live entry.</param>
    /// <returns>One or more RESP command arrays.</returns>
    public static IEnumerable<RespValue> Encode(RedisKey key, DatabaseEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var keyArg = RespValue.BulkString(key.Bytes);

        switch (entry.Type)
        {
            case RedisValueType.String:
                yield return EncodeString(keyArg, entry);
                break;
            case RedisValueType.Hash:
                foreach (var command in EncodeHash(keyArg, entry))
                {
                    yield return command;
                }

                break;
            case RedisValueType.List:
            {
                var list = entry.Value as ListValue
                    ?? throw new InvalidOperationException("List entry has unexpected runtime type.");
                if (list.Count == 0)
                {
                    yield break;
                }

                yield return EncodeList(keyArg, list);
                if (entry.ExpireAtUnixMs is long listExpire)
                {
                    yield return PexpireAt(keyArg, listExpire);
                }

                break;
            }
            case RedisValueType.Set:
            {
                var set = entry.Value as SetValue
                    ?? throw new InvalidOperationException("Set entry has unexpected runtime type.");
                if (set.Count == 0)
                {
                    yield break;
                }

                yield return EncodeSet(keyArg, set);
                if (entry.ExpireAtUnixMs is long setExpire)
                {
                    yield return PexpireAt(keyArg, setExpire);
                }

                break;
            }
            case RedisValueType.SortedSet:
            {
                var sortedSet = entry.Value as SortedSetValue
                    ?? throw new InvalidOperationException("Sorted set entry has unexpected runtime type.");
                if (sortedSet.Count == 0)
                {
                    yield break;
                }

                yield return EncodeSortedSet(keyArg, sortedSet);
                if (entry.ExpireAtUnixMs is long zExpire)
                {
                    yield return PexpireAt(keyArg, zExpire);
                }

                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported value type for AOF rewrite: {entry.Type}");
        }
    }

    private static RespValue EncodeString(RespValue keyArg, DatabaseEntry entry)
    {
        var value = entry.Value as StringValue
            ?? throw new InvalidOperationException("String entry has unexpected runtime type.");

        if (entry.ExpireAtUnixMs is long expireAt)
        {
            return RespValue.FromArray(
            [
                RespValue.BulkString("SET"),
                keyArg,
                RespValue.BulkString(value.Bytes),
                RespValue.BulkString("PXAT"),
                RespValue.BulkString(expireAt.ToString(CultureInfo.InvariantCulture))
            ]);
        }

        return RespValue.FromArray(
        [
            RespValue.BulkString("SET"),
            keyArg,
            RespValue.BulkString(value.Bytes)
        ]);
    }

    private static IEnumerable<RespValue> EncodeHash(RespValue keyArg, DatabaseEntry entry)
    {
        var hash = entry.Value as HashValue
            ?? throw new InvalidOperationException("Hash entry has unexpected runtime type.");

        if (hash.Count == 0)
        {
            yield break;
        }

        // HSET only accepts field/value pairs; emit one HSET per field to stay within handler arity.
        foreach (var field in hash.GetFields())
        {
            if (!hash.TryGetValue(field, out var value))
            {
                continue;
            }

            yield return RespValue.FromArray(
            [
                RespValue.BulkString("HSET"),
                keyArg,
                RespValue.BulkString(field),
                RespValue.BulkString(value)
            ]);
        }

        if (entry.ExpireAtUnixMs is long expireAt)
        {
            yield return PexpireAt(keyArg, expireAt);
        }
    }

    private static RespValue EncodeList(RespValue keyArg, ListValue list)
    {
        var elements = list.ToArray();
        var args = new RespValue[elements.Length + 2];
        args[0] = RespValue.BulkString("RPUSH");
        args[1] = keyArg;
        for (var i = 0; i < elements.Length; i++)
        {
            args[i + 2] = RespValue.BulkString(elements[i]);
        }

        return RespValue.FromArray(args);
    }

    private static RespValue EncodeSet(RespValue keyArg, SetValue set)
    {
        var members = set.GetMembers();
        var args = new RespValue[members.Length + 2];
        args[0] = RespValue.BulkString("SADD");
        args[1] = keyArg;
        for (var i = 0; i < members.Length; i++)
        {
            args[i + 2] = RespValue.BulkString(members[i]);
        }

        return RespValue.FromArray(args);
    }

    private static RespValue EncodeSortedSet(RespValue keyArg, SortedSetValue sortedSet)
    {
        var members = sortedSet.RangeByRankWithScores(0, sortedSet.Count - 1);
        var args = new RespValue[(members.Length * 2) + 2];
        args[0] = RespValue.BulkString("ZADD");
        args[1] = keyArg;
        for (var i = 0; i < members.Length; i++)
        {
            args[(i * 2) + 2] = RespValue.BulkString(members[i].Score.ToString("R", CultureInfo.InvariantCulture));
            args[(i * 2) + 3] = RespValue.BulkString(members[i].Member);
        }

        return RespValue.FromArray(args);
    }

    private static RespValue PexpireAt(RespValue keyArg, long expireAtUnixMs)
        => RespValue.FromArray(
        [
            RespValue.BulkString("PEXPIREAT"),
            keyArg,
            RespValue.BulkString(expireAtUnixMs.ToString(CultureInfo.InvariantCulture))
        ]);
}
