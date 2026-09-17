namespace NovaDB.Replication;

/// <summary>
/// Tracks a single replica's sync progress (reconnect token + last acknowledged offset).
/// </summary>
public interface IReplicaSession
{
    /// <summary>Gets the stable replica identifier.</summary>
    string ReplicaId { get; }

    /// <summary>Gets the reconnect token presented by the replica on handshake.</summary>
    string ReconnectToken { get; }

    /// <summary>Gets the last journal offset acknowledged by the replica.</summary>
    ulong LastAckOffset { get; }

    /// <summary>Records a successful acknowledgment from the replica.</summary>
    void Acknowledge(ulong offset);

    /// <summary>Rotates the reconnect token after a successful reconnect handshake.</summary>
    string RotateReconnectToken();
}
