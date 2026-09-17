using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NovaDB.Journal;

namespace NovaDB.Replication;

/// <summary>
/// Streams <see cref="ICommandJournal.ReadAsync"/> events through a bounded channel for backpressure.
/// </summary>
public sealed class InMemoryCommandStreamer : ICommandStreamer
{
    private readonly ICommandJournal _journal;
    private readonly int _channelCapacity;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryCommandStreamer"/> class.
    /// </summary>
    /// <param name="journal">Source journal.</param>
    /// <param name="channelCapacity">Bounded channel capacity (backpressure window).</param>
    public InMemoryCommandStreamer(ICommandJournal journal, int channelCapacity = 256)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        ArgumentOutOfRangeException.ThrowIfLessThan(channelCapacity, 1);
        _channelCapacity = channelCapacity;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CommandJournalEvent> StreamAsync(
        ulong fromExclusiveOffset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<CommandJournalEvent>(new BoundedChannelOptions(_channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var evt in _journal
                    .ReadAsync(fromExclusiveOffset, keyFilter: null, commandFilter: null, pageSize: 64, cancellationToken)
                    .ConfigureAwait(false))
                {
                    await channel.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false);
                    await channel.Writer.WriteAsync(evt, cancellationToken).ConfigureAwait(false);
                }

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, cancellationToken);

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Expected on cancel.
            }
        }
    }
}
