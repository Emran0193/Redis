using NovaDB.Persistence;

namespace NovaDB.Monitoring;

/// <summary>
/// Forwards AOF telemetry into <see cref="INovaDbMetrics"/>.
/// </summary>
public sealed class AofMetricsAdapter : IAofMetrics
{
    private readonly INovaDbMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="AofMetricsAdapter"/> class.
    /// </summary>
    public AofMetricsAdapter(INovaDbMetrics metrics)
        => _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));

    /// <inheritdoc />
    public void SetQueueDepth(long depth) => _metrics.SetAofQueueDepth(depth);

    /// <inheritdoc />
    public void SetRewriteInProgress(bool inProgress) => _metrics.SetAofRewriteInProgress(inProgress);
}
