namespace NovaDB.Storage;

/// <summary>
/// Optional storage-layer telemetry sink. Implemented by the monitoring package.
/// </summary>
public interface IStorageMetrics
{
    /// <summary>Records that a key was evicted under maxmemory.</summary>
    void RecordEviction();

    /// <summary>Records that a key was removed because it expired.</summary>
    void RecordExpiredKey();

    /// <summary>Updates the estimated memory gauge.</summary>
    /// <param name="bytes">Current estimated memory usage.</param>
    void SetMemoryBytes(long bytes);
}

/// <summary>
/// No-op metrics sink used when monitoring is not registered.
/// </summary>
public sealed class NullStorageMetrics : IStorageMetrics
{
    /// <summary>Shared singleton instance.</summary>
    public static NullStorageMetrics Instance { get; } = new();

    /// <inheritdoc />
    public void RecordEviction()
    {
    }

    /// <inheritdoc />
    public void RecordExpiredKey()
    {
    }

    /// <inheritdoc />
    public void SetMemoryBytes(long bytes)
    {
    }
}
