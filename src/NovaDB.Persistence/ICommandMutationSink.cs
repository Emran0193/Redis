namespace NovaDB.Persistence;

/// <summary>
/// Receives serialized RESP mutating commands for append-only persistence.
/// </summary>
public interface ICommandMutationSink
{
    /// <summary>
    /// Records a mutating command that was already serialized as RESP bytes.
    /// </summary>
    ValueTask OnMutatingCommandAsync(ReadOnlyMemory<byte> respBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Records multiple mutating commands; under Always flush, waits for a single group fsync.
    /// </summary>
    ValueTask OnMutatingCommandBatchAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> respBytesBatch,
        CancellationToken cancellationToken);
}
