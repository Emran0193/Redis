namespace NovaDB.Commands.Persistence;

/// <summary>
/// Optional metadata attached to a mutating command for journal / replication sinks.
/// </summary>
/// <param name="ClientId">Connection id that issued the command.</param>
/// <param name="TransactionId">EXEC correlation id when part of a transaction.</param>
/// <param name="Version">Key version at commit time.</param>
/// <param name="DedupeKey">Optional idempotency key to suppress duplicate journal writes.</param>
public readonly record struct CommandMutationMetadata(
    string ClientId,
    string TransactionId,
    ulong Version,
    string? DedupeKey)
{
    /// <summary>Empty metadata.</summary>
    public static CommandMutationMetadata Empty { get; } = new(string.Empty, string.Empty, 0, null);
}
