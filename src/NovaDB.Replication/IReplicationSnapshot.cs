namespace NovaDB.Replication;

/// <summary>
/// Describes a point-in-time snapshot used during the replication handshake
/// (snapshot id + journal offset after the snapshot was taken).
/// </summary>
public interface IReplicationSnapshot
{
    /// <summary>Gets the opaque snapshot identifier.</summary>
    string SnapshotId { get; }

    /// <summary>
    /// Gets the journal offset that is consistent with the snapshot
    /// (replica should stream journal events after this offset).
    /// </summary>
    ulong JournalOffsetAfterSnapshot { get; }

    /// <summary>Gets when the snapshot handshake descriptor was created (UTC).</summary>
    DateTimeOffset CreatedUtc { get; }
}
