using NovaDB.Journal;

namespace NovaDB.Replication;

/// <summary>
/// Streams journal events to a replica with backpressure.
/// </summary>
public interface ICommandStreamer
{
    /// <summary>
    /// Streams journal events starting after <paramref name="fromExclusiveOffset"/>.
    /// Applies channel-based backpressure so producers wait when the consumer is slow.
    /// </summary>
    IAsyncEnumerable<CommandJournalEvent> StreamAsync(
        ulong fromExclusiveOffset,
        CancellationToken cancellationToken);
}
