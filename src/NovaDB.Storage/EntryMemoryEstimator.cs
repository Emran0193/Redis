using NovaDB.Core.Models;
using NovaDB.Storage.Values;

namespace NovaDB.Storage;

/// <summary>
/// Estimates memory usage for stored keys and typed values.
/// </summary>
public static class EntryMemoryEstimator
{
    /// <summary>
    /// Estimates total memory for a key and its entry payload.
    /// </summary>
    /// <param name="key">Stored key.</param>
    /// <param name="entry">Stored entry.</param>
    /// <returns>Estimated bytes.</returns>
    public static long Estimate(RedisKey key, DatabaseEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        long size = key.Bytes.Length + MemoryEstimator.ArrayOverhead;
        size += MemoryEstimator.ObjectOverhead + 48;

        size += entry.Type switch
        {
            RedisValueType.String when entry.Value is StringValue stringValue => stringValue.EstimatedMemoryBytes,
            RedisValueType.Hash when entry.Value is HashValue hashValue => hashValue.EstimatedMemoryBytes,
            RedisValueType.List when entry.Value is ListValue listValue => listValue.EstimatedMemoryBytes,
            RedisValueType.Set when entry.Value is SetValue setValue => setValue.EstimatedMemoryBytes,
            RedisValueType.SortedSet when entry.Value is SortedSetValue sortedSetValue => sortedSetValue.EstimatedMemoryBytes,
            _ => MemoryEstimator.ObjectOverhead
        };

        return size;
    }
}
