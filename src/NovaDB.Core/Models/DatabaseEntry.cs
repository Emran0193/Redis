namespace NovaDB.Core.Models;

/// <summary>
/// Identifies the Redis-compatible value type stored under a key.
/// </summary>
public enum RedisValueType : byte
{
    /// <summary>String (bulk) value.</summary>
    String = 1,

    /// <summary>Hash map of field to value.</summary>
    Hash = 2,

    /// <summary>Linked list of values.</summary>
    List = 3,

    /// <summary>Unordered set of unique members.</summary>
    Set = 4,

    /// <summary>Ordered set of members scored by floating-point values.</summary>
    SortedSet = 5
}

/// <summary>
/// Represents a stored database entry with optional expiration and optimistic version.
/// </summary>
public sealed class DatabaseEntry
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseEntry"/> class.
    /// </summary>
    public DatabaseEntry(RedisValueType type, object value, long? expireAtUnixMs = null)
    {
        Type = type;
        Value = value ?? throw new ArgumentNullException(nameof(value));
        ExpireAtUnixMs = expireAtUnixMs;
        Version = 1;
    }

    /// <summary>Gets the value type.</summary>
    public RedisValueType Type { get; }

    /// <summary>Gets or sets the boxed typed value payload.</summary>
    public object Value { get; set; }

    /// <summary>Gets or sets absolute expiration in Unix milliseconds, or null if none.</summary>
    public long? ExpireAtUnixMs { get; set; }

    /// <summary>Gets or sets the optimistic concurrency version (WATCH).</summary>
    public long Version { get; set; }

    /// <summary>Gets or sets last access ticks for LRU.</summary>
    public long LastAccessTicks { get; set; }

    /// <summary>Gets or sets access frequency for LFU.</summary>
    public long AccessFrequency { get; set; }

    /// <summary>
    /// Gets or sets the slot this entry occupies in its shard's sampling ring.
    /// </summary>
    /// <remarks>Reserved for the storage engine; not persisted.</remarks>
    public int StorageSlot { get; set; } = -1;

    /// <summary>
    /// Returns true when the entry has expired relative to <paramref name="nowUnixMs"/>.
    /// </summary>
    public bool IsExpired(long nowUnixMs)
        => ExpireAtUnixMs is long exp && exp <= nowUnixMs;

    /// <summary>
    /// Increments the optimistic version counter.
    /// </summary>
    public void BumpVersion() => Version++;
}

/// <summary>
/// UTF-8 key wrapper used as dictionary key with ordinal comparison.
/// </summary>
public readonly struct RedisKey : IEquatable<RedisKey>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RedisKey"/> struct.
    /// </summary>
    public RedisKey(byte[] bytes)
    {
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
    }

    /// <summary>Gets the raw UTF-8 key bytes.</summary>
    public byte[] Bytes { get; }

    /// <inheritdoc />
    public bool Equals(RedisKey other) => Bytes.AsSpan().SequenceEqual(other.Bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RedisKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Bytes);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Creates a key from a UTF-8 string.
    /// </summary>
    public static RedisKey FromString(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        return new RedisKey(System.Text.Encoding.UTF8.GetBytes(value));
    }

    /// <summary>
    /// Returns the UTF-8 string representation of the key.
    /// </summary>
    public override string ToString() => System.Text.Encoding.UTF8.GetString(Bytes);
}

/// <summary>
/// Equality comparer for <see cref="RedisKey"/>.
/// </summary>
public sealed class RedisKeyComparer : IEqualityComparer<RedisKey>
{
    /// <summary>Shared singleton instance.</summary>
    public static readonly RedisKeyComparer Instance = new();

    /// <inheritdoc />
    public bool Equals(RedisKey x, RedisKey y) => x.Equals(y);

    /// <inheritdoc />
    public int GetHashCode(RedisKey obj) => obj.GetHashCode();
}
