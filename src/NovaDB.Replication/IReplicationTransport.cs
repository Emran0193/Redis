namespace NovaDB.Replication;

/// <summary>
/// Low-level transport used to exchange replication frames between primary and replica.
/// Foundation only — no leader election.
/// </summary>
public interface IReplicationTransport : IAsyncDisposable
{
    /// <summary>Gets whether the transport currently has an open session.</summary>
    bool IsConnected { get; }

    /// <summary>Opens or re-opens the transport to the configured peer.</summary>
    ValueTask ConnectAsync(CancellationToken cancellationToken);

    /// <summary>Sends a framed payload to the peer.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken);

    /// <summary>Receives the next framed payload from the peer.</summary>
    ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken);
}
