namespace NovaDB.Storage.Values;

/// <summary>
/// Redis hash value mapping binary-safe field names to byte payloads.
/// </summary>
public sealed class HashValue
{
    private readonly Dictionary<MemberKey, byte[]> _fields = [];

    /// <summary>Gets the number of fields.</summary>
    public int Count => _fields.Count;

    /// <summary>Gets the estimated memory footprint in bytes.</summary>
    public long EstimatedMemoryBytes
    {
        get
        {
            long total = MemoryEstimator.ObjectOverhead + MemoryEstimator.DictionaryEntryOverhead;
            foreach (var pair in _fields)
            {
                total += pair.Key.Bytes.Length + pair.Value.Length + MemoryEstimator.ArrayOverhead;
                total += MemoryEstimator.DictionaryEntryOverhead;
            }

            return total;
        }
    }

    /// <summary>
    /// Attempts to get a field value.
    /// </summary>
    public bool TryGetValue(byte[] field, out byte[] value)
    {
        ArgumentNullException.ThrowIfNull(field);
        return _fields.TryGetValue(new MemberKey(field), out value!);
    }

    /// <summary>
    /// Attempts to get a field value by UTF-8 field name (compat helper).
    /// </summary>
    public bool TryGetValue(string field, out byte[] value)
    {
        ArgumentException.ThrowIfNullOrEmpty(field);
        return TryGetValue(System.Text.Encoding.UTF8.GetBytes(field), out value);
    }

    /// <summary>
    /// Sets a field value, returning whether the field was newly created.
    /// </summary>
    public bool SetField(byte[] field, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);

        var key = new MemberKey(field);
        if (_fields.ContainsKey(key))
        {
            _fields[key] = value;
            return false;
        }

        _fields[key] = value;
        return true;
    }

    /// <summary>
    /// Sets a field value from a UTF-8 field name (compat helper).
    /// </summary>
    public bool SetField(string field, byte[] value)
    {
        ArgumentException.ThrowIfNullOrEmpty(field);
        return SetField(System.Text.Encoding.UTF8.GetBytes(field), value);
    }

    /// <summary>
    /// Removes a field when present.
    /// </summary>
    public bool RemoveField(byte[] field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return _fields.Remove(new MemberKey(field));
    }

    /// <summary>
    /// Removes a field by UTF-8 name (compat helper).
    /// </summary>
    public bool RemoveField(string field)
    {
        ArgumentException.ThrowIfNullOrEmpty(field);
        return RemoveField(System.Text.Encoding.UTF8.GetBytes(field));
    }

    /// <summary>
    /// Returns whether the field exists.
    /// </summary>
    public bool ContainsField(byte[] field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return _fields.ContainsKey(new MemberKey(field));
    }

    /// <summary>
    /// Gets all field names as byte arrays.
    /// </summary>
    public IReadOnlyCollection<byte[]> GetFieldBytes()
    {
        var list = new List<byte[]>(_fields.Count);
        foreach (var key in _fields.Keys)
        {
            list.Add(key.Bytes);
        }

        return list;
    }

    /// <summary>
    /// Gets all field names as UTF-8 strings (lossy for non-UTF8 fields).
    /// </summary>
    public IReadOnlyCollection<string> GetFields()
    {
        var list = new List<string>(_fields.Count);
        foreach (var key in _fields.Keys)
        {
            list.Add(System.Text.Encoding.UTF8.GetString(key.Bytes));
        }

        return list;
    }

    /// <summary>
    /// Returns all field/value pairs as alternating bulk payloads.
    /// </summary>
    public byte[][] GetAllFlat()
    {
        var result = new byte[_fields.Count * 2][];
        var index = 0;
        foreach (var pair in _fields)
        {
            result[index++] = pair.Key.Bytes;
            result[index++] = pair.Value;
        }

        return result;
    }

    /// <summary>
    /// Returns all field values as byte arrays.
    /// </summary>
    public byte[][] GetValues()
    {
        var result = new byte[_fields.Count][];
        var index = 0;
        foreach (var pair in _fields)
        {
            result[index++] = pair.Value;
        }

        return result;
    }
}
