using NovaDB.Protocol;

namespace NovaDB.Commands.Persistence;

/// <summary>
/// Receives mutating commands for AOF, journal, or replication persistence.
/// </summary>
public interface ICommandMutationSink
{
    /// <summary>
    /// Called after a mutating command completes successfully.
    /// </summary>
    ValueTask OnMutatingCommandAsync(RespValue commandArray, CancellationToken cancellationToken);

    /// <summary>
    /// Called after a mutating command completes successfully with journal metadata.
    /// Default implementation ignores metadata and delegates to the RESP-only overload.
    /// </summary>
    ValueTask OnMutatingCommandAsync(
        RespValue commandArray,
        CommandMutationMetadata metadata,
        CancellationToken cancellationToken)
        => OnMutatingCommandAsync(commandArray, cancellationToken);

    /// <summary>
    /// Persists multiple mutating commands as one durability group (single fsync under Always).
    /// </summary>
    ValueTask OnMutatingCommandBatchAsync(
        IReadOnlyList<RespValue> commandArrays,
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists a mutating batch with shared journal metadata (transaction id, client id).
    /// </summary>
    ValueTask OnMutatingCommandBatchAsync(
        IReadOnlyList<RespValue> commandArrays,
        CommandMutationMetadata metadata,
        CancellationToken cancellationToken)
        => OnMutatingCommandBatchAsync(commandArrays, cancellationToken);
}
