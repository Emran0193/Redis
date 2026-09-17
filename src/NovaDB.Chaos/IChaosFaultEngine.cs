namespace NovaDB.Chaos;

/// <summary>Supported chaos fault kinds.</summary>
public enum ChaosFaultKind
{
    /// <summary>No fault.</summary>
    None = 0,

    /// <summary>Artificial network latency.</summary>
    NetworkLatency,

    /// <summary>Drop a fraction of network operations.</summary>
    PacketLoss,

    /// <summary>Simulate disk-full failures on writes.</summary>
    DiskFull,

    /// <summary>Slow disk I/O.</summary>
    SlowDisk,

    /// <summary>Allocate memory to pressure the GC.</summary>
    MemoryPressure,

    /// <summary>Force socket disconnect semantics.</summary>
    SocketDisconnect,

    /// <summary>Corrupt AOF/journal bytes (simulation flag).</summary>
    AofCorruption,

    /// <summary>Interrupt an in-progress snapshot.</summary>
    SnapshotInterruption,

    /// <summary>Starve the thread pool with blocking work.</summary>
    ThreadPoolStarvation
}

/// <summary>
/// Injectable chaos fault engine. No-ops unless ChaosEnabled and Development environment.
/// </summary>
public interface IChaosFaultEngine
{
    /// <summary>Gets whether any fault is currently active.</summary>
    bool IsActive { get; }

    /// <summary>Gets the active fault kind, or <see cref="ChaosFaultKind.None"/>.</summary>
    ChaosFaultKind ActiveFault { get; }

    /// <summary>Gets a short description of the active fault parameters.</summary>
    string ActiveDetail { get; }

    /// <summary>
    /// Enables a fault. Throws when chaos is not permitted for this host.
    /// </summary>
    void Enable(ChaosFaultKind kind, string? detail = null);

    /// <summary>Clears all active faults.</summary>
    void Clear();

    /// <summary>Hook invoked before a network read.</summary>
    ValueTask BeforeNetworkReadAsync(CancellationToken cancellationToken);

    /// <summary>Hook invoked before a network write.</summary>
    ValueTask BeforeNetworkWriteAsync(CancellationToken cancellationToken);

    /// <summary>Hook invoked before a disk write.</summary>
    ValueTask BeforeDiskWriteAsync(CancellationToken cancellationToken);

    /// <summary>Hook invoked before snapshot work.</summary>
    ValueTask BeforeSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>Returns true when the next network packet should be dropped.</summary>
    bool ShouldDropPacket();

    /// <summary>Returns true when AOF/journal corruption should be simulated.</summary>
    bool ShouldCorruptAof();

    /// <summary>Returns true when the socket should disconnect.</summary>
    bool ShouldDisconnectSocket();
}
