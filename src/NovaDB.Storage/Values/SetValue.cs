namespace NovaDB.Storage.Values;

/// <summary>
/// Redis set value containing unique binary-safe members.
/// </summary>
public sealed class SetValue
{
    private readonly HashSet<MemberKey> _members = [];

    /// <summary>Gets the number of members.</summary>
    public int Count => _members.Count;

    /// <summary>Gets the estimated memory footprint in bytes.</summary>
    public long EstimatedMemoryBytes
    {
        get
        {
            long total = MemoryEstimator.ObjectOverhead + MemoryEstimator.DictionaryEntryOverhead;
            foreach (var member in _members)
            {
                total += member.Bytes.Length + MemoryEstimator.DictionaryEntryOverhead;
            }

            return total;
        }
    }

    /// <summary>
    /// Adds a member from raw bytes.
    /// </summary>
    public bool Add(byte[] memberBytes)
    {
        ArgumentNullException.ThrowIfNull(memberBytes);
        return _members.Add(new MemberKey(memberBytes));
    }

    /// <summary>
    /// Removes a member from raw bytes.
    /// </summary>
    public bool Remove(byte[] memberBytes)
    {
        ArgumentNullException.ThrowIfNull(memberBytes);
        return _members.Remove(new MemberKey(memberBytes));
    }

    /// <summary>
    /// Returns whether the member exists.
    /// </summary>
    public bool Contains(byte[] memberBytes)
    {
        ArgumentNullException.ThrowIfNull(memberBytes);
        return _members.Contains(new MemberKey(memberBytes));
    }

    /// <summary>
    /// Returns all members as byte arrays (copies of stored payloads).
    /// </summary>
    public byte[][] GetMembers()
    {
        var result = new byte[_members.Count][];
        var index = 0;
        foreach (var member in _members)
        {
            result[index++] = member.Bytes.ToArray();
        }

        return result;
    }
}
