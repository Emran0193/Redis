using NovaDB.Protocol;

namespace NovaDB.Commands;

/// <summary>
/// Executes a command without MULTI re-queueing (used by EXEC to avoid DI cycles).
/// </summary>
public interface ICommandExecutor
{
    /// <summary>
    /// Runs a command through handler dispatch without transaction queuing.
    /// </summary>
    /// <param name="context">Command context.</param>
    /// <param name="recordMutation">
    /// When <see langword="false"/>, skips AOF/mutation sink (caller batches durability, e.g. EXEC).
    /// </param>
    /// <returns>The RESP response.</returns>
    ValueTask<RespValue> ExecuteDirectAsync(CommandContext context, bool recordMutation = true);
}
