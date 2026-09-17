using NovaDB.Protocol;

namespace NovaDB.Commands.Persistence;

/// <summary>
/// Adapts the persistence AOF mutation sink to the commands-layer RESP value contract.
/// </summary>
public sealed class PersistenceMutationSinkAdapter : ICommandMutationSink
{
    private readonly NovaDB.Persistence.ICommandMutationSink _inner;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistenceMutationSinkAdapter"/> class.
    /// </summary>
    public PersistenceMutationSinkAdapter(NovaDB.Persistence.ICommandMutationSink inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc />
    public ValueTask OnMutatingCommandAsync(RespValue commandArray, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandArray);
        var bytes = RespWriter.Serialize(commandArray);
        return _inner.OnMutatingCommandAsync(bytes, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask OnMutatingCommandBatchAsync(
        IReadOnlyList<RespValue> commandArrays,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commandArrays);
        if (commandArrays.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        var batch = new ReadOnlyMemory<byte>[commandArrays.Count];
        for (var i = 0; i < commandArrays.Count; i++)
        {
            batch[i] = RespWriter.Serialize(commandArrays[i]);
        }

        return _inner.OnMutatingCommandBatchAsync(batch, cancellationToken);
    }
}
