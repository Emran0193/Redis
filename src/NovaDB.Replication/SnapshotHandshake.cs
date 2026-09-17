namespace NovaDB.Replication;

/// <summary>
/// Immutable handshake descriptor tying a snapshot id to the journal offset after that snapshot.
/// </summary>
public sealed class SnapshotHandshake : IReplicationSnapshot
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SnapshotHandshake"/> class.
    /// </summary>
    public SnapshotHandshake(string snapshotId, ulong journalOffsetAfterSnapshot, DateTimeOffset? createdUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);
        SnapshotId = snapshotId;
        JournalOffsetAfterSnapshot = journalOffsetAfterSnapshot;
        CreatedUtc = createdUtc ?? DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public string SnapshotId { get; }

    /// <inheritdoc />
    public ulong JournalOffsetAfterSnapshot { get; }

    /// <inheritdoc />
    public DateTimeOffset CreatedUtc { get; }

    /// <summary>
    /// Creates a handshake from the current journal watermark (no physical snapshot file yet).
    /// </summary>
    public static SnapshotHandshake FromJournalOffset(ulong offset, string? snapshotId = null)
        => new(snapshotId ?? Guid.NewGuid().ToString("N"), offset);
}
