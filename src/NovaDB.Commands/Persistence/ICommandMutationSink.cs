using NovaDB.Protocol;

namespace NovaDB.Commands.Persistence;

/// <summary>
/// Receives mutating commands for AOF or replication persistence.
/// </summary>
public interface ICommandMutationSink
{
    /// <summary>
    /// Called after a mutating command completes successfully.
    /// </summary>
    ValueTask OnMutatingCommandAsync(RespValue commandArray, CancellationToken cancellationToken);

    /// <summary>
    /// Persists multiple mutating commands as one durability group (single fsync under Always).
    /// </summary>
    ValueTask OnMutatingCommandBatchAsync(
        IReadOnlyList<RespValue> commandArrays,
        CancellationToken cancellationToken);
}
