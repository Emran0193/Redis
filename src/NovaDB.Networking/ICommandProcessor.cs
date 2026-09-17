using NovaDB.Protocol;

namespace NovaDB.Networking;

/// <summary>
/// Processes parsed RESP command requests for a client connection.
/// </summary>
public interface ICommandProcessor
{
    /// <summary>
    /// Handles a single RESP request and returns the response to send to the client.
    /// </summary>
    /// <param name="connection">The client connection executing the command.</param>
    /// <param name="request">The parsed RESP request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The RESP response.</returns>
    ValueTask<RespValue> ProcessAsync(IClientConnection connection, RespValue request, CancellationToken cancellationToken);

    /// <summary>
    /// Releases all per-connection state held by the command layer when a socket goes away.
    /// </summary>
    /// <param name="connectionId">The disconnecting connection's identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask OnDisconnectedAsync(string connectionId, CancellationToken cancellationToken);
}
