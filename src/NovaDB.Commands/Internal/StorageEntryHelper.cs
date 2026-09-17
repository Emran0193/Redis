using NovaDB.Core.Exceptions;
using NovaDB.Core.Models;
using NovaDB.Storage.Values;

namespace NovaDB.Commands.Internal;

/// <summary>
/// Helpers for reading and validating typed storage entries.
/// </summary>
internal static class StorageEntryHelper
{
    /// <summary>
    /// Throws <see cref="WrongTypeException"/> when the entry type does not match.
    /// </summary>
    public static void EnsureType(DatabaseEntry entry, RedisValueType expected)
    {
        if (entry.Type != expected)
        {
            throw new WrongTypeException();
        }
    }

    /// <summary>
    /// Gets the string value payload from an entry.
    /// </summary>
    public static StringValue GetStringValue(DatabaseEntry entry)
    {
        EnsureType(entry, RedisValueType.String);
        return (StringValue)entry.Value;
    }

    /// <summary>
    /// Gets the hash value payload from an entry.
    /// </summary>
    public static HashValue GetHashValue(DatabaseEntry entry)
    {
        EnsureType(entry, RedisValueType.Hash);
        return (HashValue)entry.Value;
    }

    /// <summary>
    /// Gets the list value payload from an entry.
    /// </summary>
    public static ListValue GetListValue(DatabaseEntry entry)
    {
        EnsureType(entry, RedisValueType.List);
        return (ListValue)entry.Value;
    }

    /// <summary>
    /// Gets the set value payload from an entry.
    /// </summary>
    public static SetValue GetSetValue(DatabaseEntry entry)
    {
        EnsureType(entry, RedisValueType.Set);
        return (SetValue)entry.Value;
    }

    /// <summary>
    /// Gets the sorted set value payload from an entry.
    /// </summary>
    public static SortedSetValue GetSortedSetValue(DatabaseEntry entry)
    {
        EnsureType(entry, RedisValueType.SortedSet);
        return (SortedSetValue)entry.Value;
    }

    /// <summary>
    /// Creates a new database entry with the given type and value.
    /// </summary>
    public static DatabaseEntry CreateEntry(RedisValueType type, object value, long? expireAtUnixMs = null)
        => new(type, value, expireAtUnixMs);

    /// <summary>
    /// Returns current Unix time in milliseconds.
    /// </summary>
    public static long NowUnixMs()
        => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
