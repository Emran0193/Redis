using System.Text;
using NovaDB.Journal;
using NovaDB.Protocol;

namespace NovaDB.Commands.Persistence;

/// <summary>
/// Records mutating commands into the durable command journal.
/// </summary>
public sealed class JournalMutationSink : ICommandMutationSink
{
    private readonly ICommandJournal _journal;

    /// <summary>
    /// Initializes a new instance of the <see cref="JournalMutationSink"/> class.
    /// </summary>
    public JournalMutationSink(ICommandJournal journal)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    }

    /// <inheritdoc />
    public ValueTask OnMutatingCommandAsync(RespValue commandArray, CancellationToken cancellationToken)
        => OnMutatingCommandAsync(commandArray, CommandMutationMetadata.Empty, cancellationToken);

    /// <inheritdoc />
    public async ValueTask OnMutatingCommandAsync(
        RespValue commandArray,
        CommandMutationMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandArray);
        if (commandArray.Type != RespType.Array || commandArray.Array is null || commandArray.Array.Length == 0)
        {
            return;
        }

        var parts = commandArray.Array;
        var command = parts[0].AsUtf8String().ToUpperInvariant();
        var key = parts.Length > 1 && parts[1].Bulk is not null
            ? (ReadOnlyMemory<byte>)parts[1].Bulk
            : ReadOnlyMemory<byte>.Empty;

        var args = new ReadOnlyMemory<byte>[Math.Max(0, parts.Length - 1)];
        for (var i = 1; i < parts.Length; i++)
        {
            args[i - 1] = parts[i].Bulk ?? Encoding.UTF8.GetBytes(parts[i].AsUtf8String());
        }

        await _journal.AppendAsync(
            metadata.ClientId,
            metadata.TransactionId,
            command,
            key,
            args,
            metadata.Version,
            metadata.DedupeKey,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask OnMutatingCommandBatchAsync(
        IReadOnlyList<RespValue> commandArrays,
        CancellationToken cancellationToken)
        => OnMutatingCommandBatchAsync(commandArrays, CommandMutationMetadata.Empty, cancellationToken);

    /// <inheritdoc />
    public async ValueTask OnMutatingCommandBatchAsync(
        IReadOnlyList<RespValue> commandArrays,
        CommandMutationMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandArrays);
        for (var i = 0; i < commandArrays.Count; i++)
        {
            var itemMeta = metadata with
            {
                TransactionId = string.IsNullOrEmpty(metadata.TransactionId)
                    ? metadata.ClientId
                    : metadata.TransactionId,
                DedupeKey = metadata.DedupeKey is null ? null : $"{metadata.DedupeKey}:{i}"
            };
            await OnMutatingCommandAsync(commandArrays[i], itemMeta, cancellationToken).ConfigureAwait(false);
        }
    }
}
