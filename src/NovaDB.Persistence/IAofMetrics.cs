namespace NovaDB.Persistence;

/// <summary>
/// Optional AOF-layer telemetry sink. Implemented by the monitoring package.
/// </summary>
public interface IAofMetrics
{
    /// <summary>Updates the pending AOF write-queue depth gauge.</summary>
    void SetQueueDepth(long depth);

    /// <summary>Updates whether an AOF rewrite is in progress (0 or 1).</summary>
    void SetRewriteInProgress(bool inProgress);
}

/// <summary>
/// No-op metrics sink used when monitoring is not registered.
/// </summary>
public sealed class NullAofMetrics : IAofMetrics
{
    /// <summary>Shared singleton instance.</summary>
    public static NullAofMetrics Instance { get; } = new();

    /// <inheritdoc />
    public void SetQueueDepth(long depth)
    {
    }

    /// <inheritdoc />
    public void SetRewriteInProgress(bool inProgress)
    {
    }
}
