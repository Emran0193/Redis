using NovaDB.Core.Sessions;
using NovaDB.Networking;
using NovaDB.Protocol;

namespace NovaDB.Commands;

/// <summary>
/// Context for a single command execution including connection, session, and arguments.
/// </summary>
public sealed class CommandContext
{
    /// <summary>Gets the client connection.</summary>
    public required IClientConnection Connection { get; init; }

    /// <summary>Gets the per-connection session state.</summary>
    public required ClientSession Session { get; init; }

    /// <summary>Gets command arguments where <c>args[0]</c> is the command name bulk.</summary>
    public required RespValue[] Arguments { get; init; }

    /// <summary>Gets the service provider for optional dependencies.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>Gets the cancellation token.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Gets a value indicating whether this command is being replayed from the append-only log,
    /// in which case it must not be written back to that log.
    /// </summary>
    public bool IsReplay { get; init; }

    /// <summary>Gets the uppercase command name.</summary>
    public string CommandName
        => Arguments.Length == 0 ? string.Empty : Arguments[0].AsUtf8String().ToUpperInvariant();
}
