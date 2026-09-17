namespace NovaDB.Storage.Values;

/// <summary>
/// Shared constants for approximate in-memory size accounting.
/// </summary>
internal static class MemoryEstimator
{
    /// <summary>Approximate object header overhead.</summary>
    public const int ObjectOverhead = 24;

    /// <summary>Approximate array header overhead.</summary>
    public const int ArrayOverhead = 24;

    /// <summary>Approximate dictionary entry overhead.</summary>
    public const int DictionaryEntryOverhead = 64;

    /// <summary>Approximate linked-list node overhead.</summary>
    public const int LinkedListNodeOverhead = 32;
}
