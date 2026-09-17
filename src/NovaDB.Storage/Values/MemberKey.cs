namespace NovaDB.Storage.Values;

/// <summary>
/// Binary-safe dictionary key for set / sorted-set / hash field members.
/// </summary>
public readonly struct MemberKey : IEquatable<MemberKey>
{
    private readonly byte[] _bytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemberKey"/> struct.
    /// </summary>
    /// <param name="bytes">Member bytes (stored by reference; caller must not mutate).</param>
    public MemberKey(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        _bytes = bytes;
    }

    /// <summary>Gets the member bytes.</summary>
    public byte[] Bytes => _bytes;

    /// <inheritdoc />
    public bool Equals(MemberKey other) => _bytes.AsSpan().SequenceEqual(other._bytes);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MemberKey other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(_bytes);
        return hash.ToHashCode();
    }
}
