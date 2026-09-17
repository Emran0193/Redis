using NovaDB.Protocol;

namespace NovaDB.Commands;

/// <summary>
/// Executes a single Redis-compatible command.
/// </summary>
public interface ICommandHandler
{
    /// <summary>Gets the uppercase command name (e.g. GET, SET).</summary>
    string Name { get; }

    /// <summary>
    /// Executes the command against the given context.
    /// </summary>
    /// <param name="context">Command execution context.</param>
    /// <returns>The RESP response.</returns>
    ValueTask<RespValue> ExecuteAsync(CommandContext context);
}
