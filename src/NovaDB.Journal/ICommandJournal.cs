namespace NovaDB.Journal;

/// <summary>
/// Durable append-only command journal used for replication streaming and time-travel inspection.
/// </summary>
public interface ICommandJournal
{
    /// <summary>Gets the path of the active journal file.</summary>
    string FilePath { get; }

    /// <summary>Gets the last successfully appended event id (replication offset).</summary>
    ulong CurrentOffset { get; }

    /// <summary>Gets the on-disk journal size in bytes.</summary>
    long SizeBytes { get; }

    /// <summary>
    /// Appends a mutating command event. Event ids are assigned monotonically by the journal.
    /// Duplicate logical writes are rejected when <paramref name="dedupeKey"/> matches the previous append.
    /// </summary>
    ValueTask<CommandJournalEvent> AppendAsync(
        string clientId,
        string transactionId,
        string command,
        ReadOnlyMemory<byte> key,
        IReadOnlyList<ReadOnlyMemory<byte>> arguments,
        ulong version,
        string? dedupeKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Streams events starting at <paramref name="fromExclusiveOffset"/> (exclusive) with optional filters.
    /// Never materializes the entire journal.
    /// </summary>
    IAsyncEnumerable<CommandJournalEvent> ReadAsync(
        ulong fromExclusiveOffset,
        string? keyFilter,
        string? commandFilter,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replays all valid events from the start of the file, invoking <paramref name="onEvent"/> for each.
    /// Stops at the first CRC failure.
    /// </summary>
    Task ReplayAsync(Func<CommandJournalEvent, CancellationToken, ValueTask> onEvent, CancellationToken cancellationToken);

    /// <summary>Forces buffered data to durable storage.</summary>
    Task FlushAsync(CancellationToken cancellationToken);
}
