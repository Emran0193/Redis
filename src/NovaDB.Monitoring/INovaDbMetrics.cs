namespace NovaDB.Monitoring;

/// <summary>
/// Records NovaDB operational metrics exposed via OpenTelemetry.
/// </summary>
public interface INovaDbMetrics
{
    /// <summary>
    /// Records command execution latency.
    /// </summary>
    /// <param name="command">Command name.</param>
    /// <param name="elapsedMilliseconds">Elapsed time in milliseconds.</param>
    void RecordCommandLatency(string command, double elapsedMilliseconds);

    /// <summary>
    /// Increments the connected client count.
    /// </summary>
    void IncrementConnectedClients();

    /// <summary>
    /// Decrements the connected client count.
    /// </summary>
    void DecrementConnectedClients();

    /// <summary>
    /// Updates the estimated memory usage gauge.
    /// </summary>
    /// <param name="bytes">Estimated memory usage in bytes.</param>
    void SetMemoryBytes(long bytes);

    /// <summary>
    /// Records a cache hit.
    /// </summary>
    void RecordCacheHit();

    /// <summary>
    /// Records a cache miss.
    /// </summary>
    void RecordCacheMiss();

    /// <summary>
    /// Records a key eviction.
    /// </summary>
    void RecordEviction();

    /// <summary>
    /// Records an expired key removal.
    /// </summary>
    void RecordExpiredKey();

    /// <summary>
    /// Updates the pending AOF write-queue depth gauge.
    /// </summary>
    void SetAofQueueDepth(long depth);

    /// <summary>
    /// Updates whether an AOF rewrite is currently running.
    /// </summary>
    void SetAofRewriteInProgress(bool inProgress);

    /// <summary>Records AOF flush latency in milliseconds.</summary>
    void RecordAofFlushLatency(double elapsedMilliseconds);

    /// <summary>Updates durable journal size gauge.</summary>
    void SetJournalSizeBytes(long bytes);

    /// <summary>Updates replication lag (primary offset − replica ack) gauge.</summary>
    void SetReplicationLag(long lag);

    /// <summary>Updates estimated socket accept backlog gauge.</summary>
    void SetSocketBacklog(long backlog);

    /// <summary>Updates thread-pool queue length gauge.</summary>
    void SetThreadPoolQueueLength(long length);

    /// <summary>Records a GC pause duration in milliseconds.</summary>
    void RecordGcPause(double elapsedMilliseconds);

    /// <summary>Gets a point-in-time snapshot of counters for INFO / MEMORY.</summary>
    NovaDbMetricsSnapshot GetSnapshot();
}

/// <summary>Point-in-time counters used by INFO and MEMORY.</summary>
/// <param name="ConnectedClients">Active TCP clients.</param>
/// <param name="MemoryBytes">Estimated dataset memory.</param>
/// <param name="CacheHits">GET/MGET hits.</param>
/// <param name="CacheMisses">GET/MGET misses.</param>
/// <param name="Evictions">maxmemory evictions.</param>
/// <param name="ExpiredKeys">Expired key removals.</param>
public readonly record struct NovaDbMetricsSnapshot(
    long ConnectedClients,
    long MemoryBytes,
    long CacheHits,
    long CacheMisses,
    long Evictions,
    long ExpiredKeys);
