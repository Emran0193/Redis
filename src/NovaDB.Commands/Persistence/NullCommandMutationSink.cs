using NovaDB.Protocol;

namespace NovaDB.Commands.Persistence;

/// <summary>
/// No-op mutation sink used when persistence is disabled.
/// </summary>
public sealed class NullCommandMutationSink : ICommandMutationSink
{
    /// <summary>Shared singleton instance.</summary>
    public static NullCommandMutationSink Instance { get; } = new();

    /// <inheritdoc />
    public ValueTask OnMutatingCommandAsync(RespValue commandArray, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnMutatingCommandBatchAsync(
        IReadOnlyList<RespValue> commandArrays,
        CancellationToken cancellationToken)
        => ValueTask.CompletedTask;
}
