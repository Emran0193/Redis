namespace NovaDB.Storage.Values;

/// <summary>
/// Redis list value backed by a doubly-linked list of byte arrays.
/// </summary>
public sealed class ListValue
{
    private readonly LinkedList<byte[]> _elements = [];

    /// <summary>Gets the number of elements.</summary>
    public int Count => _elements.Count;

    /// <summary>Gets the estimated memory footprint in bytes.</summary>
    public long EstimatedMemoryBytes
    {
        get
        {
            long total = MemoryEstimator.ObjectOverhead;
            for (var node = _elements.First; node is not null; node = node.Next)
            {
                total += node.Value.Length + MemoryEstimator.ArrayOverhead + MemoryEstimator.LinkedListNodeOverhead;
            }

            return total;
        }
    }

    /// <summary>
    /// Pushes a value to the head of the list.
    /// </summary>
    /// <param name="value">Value bytes.</param>
    public void PushHead(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _elements.AddFirst(value);
    }

    /// <summary>
    /// Pushes a value to the tail of the list.
    /// </summary>
    /// <param name="value">Value bytes.</param>
    public void PushTail(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _elements.AddLast(value);
    }

    /// <summary>
    /// Removes and returns the head value.
    /// </summary>
    /// <param name="value">Removed value when present.</param>
    /// <returns><see langword="true"/> when a value was removed.</returns>
    public bool TryPopHead(out byte[] value)
    {
        if (_elements.First is null)
        {
            value = [];
            return false;
        }

        value = _elements.First.Value;
        _elements.RemoveFirst();
        return true;
    }

    /// <summary>
    /// Removes and returns the tail value.
    /// </summary>
    /// <param name="value">Removed value when present.</param>
    /// <returns><see langword="true"/> when a value was removed.</returns>
    public bool TryPopTail(out byte[] value)
    {
        if (_elements.Last is null)
        {
            value = [];
            return false;
        }

        value = _elements.Last.Value;
        _elements.RemoveLast();
        return true;
    }

    /// <summary>
    /// Gets the element at the specified zero-based index.
    /// </summary>
    /// <param name="index">Element index.</param>
    /// <returns>Element bytes.</returns>
    public byte[] GetAt(long index)
    {
        if (index < 0 || index >= _elements.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var node = index <= _elements.Count / 2 ? _elements.First : _elements.Last;
        var step = index <= _elements.Count / 2 ? 1 : -1;
        var position = index <= _elements.Count / 2 ? 0L : _elements.Count - 1;

        while (node is not null && position != index)
        {
            node = step > 0 ? node.Next : node.Previous;
            position += step;
        }

        return node?.Value ?? throw new InvalidOperationException("List index walk failed.");
    }

    /// <summary>
    /// Sets the element at the specified zero-based index.
    /// </summary>
    /// <param name="index">Element index.</param>
    /// <param name="value">New value bytes.</param>
    public void SetAt(long index, byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (index < 0 || index >= _elements.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var node = index <= _elements.Count / 2 ? _elements.First : _elements.Last;
        var step = index <= _elements.Count / 2 ? 1 : -1;
        var position = index <= _elements.Count / 2 ? 0L : _elements.Count - 1;

        while (node is not null && position != index)
        {
            node = step > 0 ? node.Next : node.Previous;
            position += step;
        }

        if (node is null)
        {
            throw new InvalidOperationException("List index walk failed.");
        }

        node.Value = value;
    }

    /// <summary>
    /// Returns a snapshot of all elements in list order.
    /// </summary>
    /// <returns>Element bytes.</returns>
    public byte[][] ToArray()
    {
        var result = new byte[_elements.Count][];
        var index = 0;
        for (var node = _elements.First; node is not null; node = node.Next)
        {
            result[index++] = node.Value;
        }

        return result;
    }

    /// <summary>
    /// Returns a sub-range of elements (LRANGE semantics with negative indexes).
    /// </summary>
    /// <param name="start">Start index.</param>
    /// <param name="stop">Stop index inclusive.</param>
    /// <returns>Selected elements.</returns>
    public byte[][] Range(long start, long stop)
    {
        var count = _elements.Count;
        if (count == 0)
        {
            return [];
        }

        if (start < 0)
        {
            start = count + start;
        }

        if (stop < 0)
        {
            stop = count + stop;
        }

        if (start < 0)
        {
            start = 0;
        }

        if (stop >= count)
        {
            stop = count - 1;
        }

        if (start > stop || start >= count)
        {
            return [];
        }

        var length = (int)(stop - start + 1);
        var result = new byte[length][];
        var node = _elements.First;
        long position = 0;
        while (node is not null && position < start)
        {
            node = node.Next;
            position++;
        }

        for (var i = 0; i < length && node is not null; i++)
        {
            result[i] = node.Value;
            node = node.Next;
        }

        return result;
    }
}
