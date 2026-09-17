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
    private readonly Histogram<double> _aofFlushLatency;
    private readonly Histogram<double> _gcPause;
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
    private long _journalSizeBytes;
    private long _replicationLag;
    private long _socketBacklog;
    private long _threadPoolQueueLength;

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

        _aofFlushLatency = _meter.CreateHistogram<double>(
            "novadb.aof.flush_latency",
            unit: "ms",
            description: "AOF flush latency in milliseconds");

        _gcPause = _meter.CreateHistogram<double>(
            "novadb.gc.pause",
            unit: "ms",
            description: "GC pause duration in milliseconds");

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

        _meter.CreateObservableGauge(
            "novadb.journal.size_bytes",
            () => Volatile.Read(ref _journalSizeBytes),
            unit: "bytes",
            description: "Durable command journal size");

        _meter.CreateObservableGauge(
            "novadb.replication.lag",
            () => Volatile.Read(ref _replicationLag),
            description: "Replication lag in journal events");

        _meter.CreateObservableGauge(
            "novadb.socket.backlog",
            () => Volatile.Read(ref _socketBacklog),
            description: "Estimated socket accept backlog");

        _meter.CreateObservableGauge(
            "novadb.threadpool.queue_length",
            () => Volatile.Read(ref _threadPoolQueueLength),
            description: "Thread-pool work-item queue length estimate");
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
    public void RecordAofFlushLatency(double elapsedMilliseconds)
        => _aofFlushLatency.Record(elapsedMilliseconds);

    /// <inheritdoc />
    public void SetJournalSizeBytes(long bytes) => Interlocked.Exchange(ref _journalSizeBytes, bytes);

    /// <inheritdoc />
    public void SetReplicationLag(long lag) => Interlocked.Exchange(ref _replicationLag, lag);

    /// <inheritdoc />
    public void SetSocketBacklog(long backlog) => Interlocked.Exchange(ref _socketBacklog, backlog);

    /// <inheritdoc />
    public void SetThreadPoolQueueLength(long length)
        => Interlocked.Exchange(ref _threadPoolQueueLength, length);

    /// <inheritdoc />
    public void RecordGcPause(double elapsedMilliseconds) => _gcPause.Record(elapsedMilliseconds);

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
