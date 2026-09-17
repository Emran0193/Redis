namespace NovaDB.Journal;

/// <summary>
/// Immutable journal event describing one mutating command.
/// </summary>
/// <param name="EventId">Monotonic append offset (replication offset).</param>
/// <param name="TimestampUtc">Wall-clock time when the mutation was accepted.</param>
/// <param name="ClientId">Connection identifier that issued the command.</param>
/// <param name="TransactionId">Optional EXEC batch correlation id; empty when not in a transaction.</param>
/// <param name="Command">Uppercase command name (SET, DEL, …).</param>
/// <param name="Arguments">Command arguments excluding the command name, as UTF-8 byte arrays.</param>
/// <param name="Key">Primary key bytes when present; empty otherwise.</param>
/// <param name="Version">Storage key version at commit time, or 0 when unknown.</param>
/// <param name="Checksum">CRC32 of the serialized payload (excluding the checksum field itself).</param>
public sealed record CommandJournalEvent(
    ulong EventId,
    DateTimeOffset TimestampUtc,
    string ClientId,
    string TransactionId,
    string Command,
    IReadOnlyList<ReadOnlyMemory<byte>> Arguments,
    ReadOnlyMemory<byte> Key,
    ulong Version,
    uint Checksum);
