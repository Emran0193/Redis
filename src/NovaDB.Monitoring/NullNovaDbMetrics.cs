namespace NovaDB.Monitoring;

/// <summary>
/// No-op metrics implementation used by tests and hosts that skip OpenTelemetry.
/// </summary>
public sealed class NullNovaDbMetrics : INovaDbMetrics
{
    /// <summary>Shared singleton.</summary>
    public static NullNovaDbMetrics Instance { get; } = new();

    /// <inheritdoc />
    public void RecordCommandLatency(string command, double elapsedMilliseconds)
    {
    }

    /// <inheritdoc />
    public void IncrementConnectedClients()
    {
    }

    /// <inheritdoc />
    public void DecrementConnectedClients()
    {
    }

    /// <inheritdoc />
    public void SetMemoryBytes(long bytes)
    {
    }

    /// <inheritdoc />
    public void RecordCacheHit()
    {
    }

    /// <inheritdoc />
    public void RecordCacheMiss()
    {
    }

    /// <inheritdoc />
    public void RecordEviction()
    {
    }

    /// <inheritdoc />
    public void RecordExpiredKey()
    {
    }

    /// <inheritdoc />
    public void SetAofQueueDepth(long depth)
    {
    }

    /// <inheritdoc />
    public void SetAofRewriteInProgress(bool inProgress)
    {
    }

    /// <inheritdoc />
    public NovaDbMetricsSnapshot GetSnapshot() => default;
}
