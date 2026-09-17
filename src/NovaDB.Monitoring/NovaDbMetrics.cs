using System.Diagnostics.Metrics;

namespace NovaDB.Monitoring;

/// <summary>
/// OpenTelemetry metrics instrumentation for NovaDB.
/// </summary>
public sealed class NovaDbMetrics : INovaDbMetrics, IDisposable
{
    /// <summary>OpenTelemetry meter name used by NovaDB.</summary>
    public const string MeterName = "NovaDB";

    private readonly Meter _meter;
    private readonly Histogram<double> _commandLatency;
    private readonly UpDownCounter<long> _connectedClientsMetric;
    private readonly Counter<long> _cacheHitsMetric;
    private readonly Counter<long> _cacheMissesMetric;
    private readonly Counter<long> _evictionsMetric;
    private readonly Counter<long> _expiredKeysMetric;
    private long _connectedClients;
    private long _memoryBytes;
    private long _cacheHits;
    private long _cacheMisses;
    private long _evictions;
    private long _expiredKeys;
    private long _aofQueueDepth;
    private long _aofRewriteInProgress;

    /// <summary>
    /// Initializes a new instance of the <see cref="NovaDbMetrics"/> class.
    /// </summary>
    public NovaDbMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        _commandLatency = _meter.CreateHistogram<double>(
            "novadb.command.latency",
            unit: "ms",
            description: "Command execution latency in milliseconds");

        _connectedClientsMetric = _meter.CreateUpDownCounter<long>(
            "novadb.clients.connected",
            description: "Number of connected clients");

        _cacheHitsMetric = _meter.CreateCounter<long>(
            "novadb.cache.hits",
            description: "Number of cache hits");

        _cacheMissesMetric = _meter.CreateCounter<long>(
            "novadb.cache.misses",
            description: "Number of cache misses");

        _evictionsMetric = _meter.CreateCounter<long>(
            "novadb.keys.evicted",
            description: "Number of keys evicted");

        _expiredKeysMetric = _meter.CreateCounter<long>(
            "novadb.keys.expired",
            description: "Number of keys expired");

        _meter.CreateObservableGauge(
            "novadb.memory.bytes",
            () => Volatile.Read(ref _memoryBytes),
            unit: "bytes",
            description: "Estimated memory usage in bytes");

        _meter.CreateObservableGauge(
            "novadb.aof.queue_depth",
            () => Volatile.Read(ref _aofQueueDepth),
            description: "Pending AOF write-queue depth");

        _meter.CreateObservableGauge(
            "novadb.aof.rewrite_in_progress",
            () => Volatile.Read(ref _aofRewriteInProgress),
            description: "1 while an AOF rewrite is running");
    }

    /// <inheritdoc />
    public void RecordCommandLatency(string command, double elapsedMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        _commandLatency.Record(
            elapsedMilliseconds,
            new KeyValuePair<string, object?>("command", command));
    }

    /// <inheritdoc />
    public void IncrementConnectedClients()
    {
        Interlocked.Increment(ref _connectedClients);
        _connectedClientsMetric.Add(1);
    }

    /// <inheritdoc />
    public void DecrementConnectedClients()
    {
        Interlocked.Decrement(ref _connectedClients);
        _connectedClientsMetric.Add(-1);
    }

    /// <inheritdoc />
    public void SetMemoryBytes(long bytes) => Interlocked.Exchange(ref _memoryBytes, bytes);

    /// <inheritdoc />
    public void RecordCacheHit()
    {
        Interlocked.Increment(ref _cacheHits);
        _cacheHitsMetric.Add(1);
    }

    /// <inheritdoc />
    public void RecordCacheMiss()
    {
        Interlocked.Increment(ref _cacheMisses);
        _cacheMissesMetric.Add(1);
    }

    /// <inheritdoc />
    public void RecordEviction()
    {
        Interlocked.Increment(ref _evictions);
        _evictionsMetric.Add(1);
    }

    /// <inheritdoc />
    public void RecordExpiredKey()
    {
        Interlocked.Increment(ref _expiredKeys);
        _expiredKeysMetric.Add(1);
    }

    /// <inheritdoc />
    public void SetAofQueueDepth(long depth) => Interlocked.Exchange(ref _aofQueueDepth, depth);

    /// <inheritdoc />
    public void SetAofRewriteInProgress(bool inProgress)
        => Interlocked.Exchange(ref _aofRewriteInProgress, inProgress ? 1 : 0);

    /// <inheritdoc />
    public NovaDbMetricsSnapshot GetSnapshot()
        => new(
            Volatile.Read(ref _connectedClients),
            Volatile.Read(ref _memoryBytes),
            Volatile.Read(ref _cacheHits),
            Volatile.Read(ref _cacheMisses),
            Volatile.Read(ref _evictions),
            Volatile.Read(ref _expiredKeys));

    /// <inheritdoc />
    public void Dispose() => _meter.Dispose();
}
