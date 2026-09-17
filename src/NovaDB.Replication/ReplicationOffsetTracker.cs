using NovaDB.Journal;

namespace NovaDB.Replication;

/// <summary>
/// Thin wrapper around <see cref="ICommandJournal.CurrentOffset"/> for replication lag tracking.
/// </summary>
public sealed class ReplicationOffsetTracker
{
    private readonly ICommandJournal _journal;
    private long _replicaAckOffset;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReplicationOffsetTracker"/> class.
    /// </summary>
    public ReplicationOffsetTracker(ICommandJournal journal)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    /// <summary>Gets the primary's current journal offset.</summary>
    public ulong PrimaryOffset => _journal.CurrentOffset;

    /// <summary>Gets the last offset acknowledged by any tracked replica.</summary>
    public ulong ReplicaAckOffset => (ulong)Volatile.Read(ref _replicaAckOffset);

    /// <summary>Gets how many journal events the replica has not yet acknowledged.</summary>
    public ulong Lag => PrimaryOffset > ReplicaAckOffset ? PrimaryOffset - ReplicaAckOffset : 0;

    /// <summary>Updates the replica acknowledgment watermark.</summary>
    public void SetReplicaAck(ulong offset)
        => Interlocked.Exchange(ref _replicaAckOffset, (long)offset);
}
