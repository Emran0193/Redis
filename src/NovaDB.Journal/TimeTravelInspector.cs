using System.Runtime.CompilerServices;
using System.Text;

namespace NovaDB.Journal;

/// <summary>
/// Time-travel inspection over the durable command journal (paginated, never full-loads).
/// </summary>
public interface ITimeTravelInspector
{
    /// <summary>
    /// Streams matching events for <paramref name="key"/> starting after <paramref name="fromExclusiveOffset"/>.
    /// </summary>
    IAsyncEnumerable<CommandJournalEvent> SearchByKeyAsync(
        string key,
        ulong fromExclusiveOffset,
        int pageSize,
        CancellationToken cancellationToken);

    /// <summary>
    /// Builds a human-readable diff between two consecutive events for the same key.
    /// </summary>
    TimeTravelDiff Diff(CommandJournalEvent? previous, CommandJournalEvent current);
}

/// <summary>Payload preview for a journal event argument list.</summary>
/// <param name="HexPreview">Hex-encoded truncated payload.</param>
/// <param name="Utf8Preview">Best-effort UTF-8 preview.</param>
public sealed record TimeTravelPayloadPreview(string HexPreview, string Utf8Preview);

/// <summary>Diff of consecutive key mutations.</summary>
/// <param name="Previous">Previous event payload preview (null when no prior event).</param>
/// <param name="Current">Current event payload preview.</param>
/// <param name="CommandChanged">Whether the command name changed.</param>
public sealed record TimeTravelDiff(
    TimeTravelPayloadPreview? Previous,
    TimeTravelPayloadPreview Current,
    bool CommandChanged);

/// <summary>
/// Default <see cref="ITimeTravelInspector"/> implementation.
/// </summary>
public sealed class TimeTravelInspector : ITimeTravelInspector
{
    private const int PreviewBytes = 64;
    private readonly ICommandJournal _journal;

    /// <summary>
    /// Initializes a new instance of the <see cref="TimeTravelInspector"/> class.
    /// </summary>
    public TimeTravelInspector(ICommandJournal journal)
        => _journal = journal ?? throw new ArgumentNullException(nameof(journal));

    /// <inheritdoc />
    public async IAsyncEnumerable<CommandJournalEvent> SearchByKeyAsync(
        string key,
        ulong fromExclusiveOffset,
        int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (pageSize <= 0)
        {
            pageSize = 50;
        }

        pageSize = Math.Min(pageSize, 500);
        var yielded = 0;
        await foreach (var evt in _journal
            .ReadAsync(fromExclusiveOffset, keyFilter: key, commandFilter: null, pageSize: pageSize, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return evt;
            yielded++;
            if (yielded >= pageSize)
            {
                yield break;
            }
        }
    }

    /// <inheritdoc />
    public TimeTravelDiff Diff(CommandJournalEvent? previous, CommandJournalEvent current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return new TimeTravelDiff(
            previous is null ? null : Preview(previous),
            Preview(current),
            previous is not null
                && !string.Equals(previous.Command, current.Command, StringComparison.Ordinal));
    }

    private static TimeTravelPayloadPreview Preview(CommandJournalEvent evt)
    {
        var combined = CombineArgs(evt.Arguments);
        var slice = combined.Length <= PreviewBytes ? combined : combined.AsSpan(0, PreviewBytes).ToArray();
        var hex = Convert.ToHexString(slice);
        string utf8;
        try
        {
            utf8 = Encoding.UTF8.GetString(slice);
        }
        catch (DecoderFallbackException)
        {
            utf8 = string.Empty;
        }

        return new TimeTravelPayloadPreview(hex, utf8);
    }

    private static byte[] CombineArgs(IReadOnlyList<ReadOnlyMemory<byte>> arguments)
    {
        if (arguments.Count == 0)
        {
            return [];
        }

        var total = 0;
        foreach (var a in arguments)
        {
            total += a.Length + 1;
        }

        var buffer = new byte[total];
        var offset = 0;
        foreach (var a in arguments)
        {
            a.Span.CopyTo(buffer.AsSpan(offset));
            offset += a.Length;
            if (offset < buffer.Length)
            {
                buffer[offset++] = (byte)' ';
            }
        }

        return buffer;
    }
}
