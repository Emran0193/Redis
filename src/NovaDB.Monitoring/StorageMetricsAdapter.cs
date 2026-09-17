using NovaDB.Storage;

namespace NovaDB.Monitoring;

/// <summary>
/// Forwards storage telemetry into <see cref="INovaDbMetrics"/>.
/// </summary>
internal sealed class StorageMetricsAdapter : IStorageMetrics
{
    private readonly INovaDbMetrics _metrics;

    public StorageMetricsAdapter(INovaDbMetrics metrics)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <inheritdoc />
    public void RecordEviction() => _metrics.RecordEviction();

    /// <inheritdoc />
    public void RecordExpiredKey() => _metrics.RecordExpiredKey();

    /// <inheritdoc />
    public void SetMemoryBytes(long bytes) => _metrics.SetMemoryBytes(bytes);
}
