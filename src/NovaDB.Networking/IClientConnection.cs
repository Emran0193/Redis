using NovaDB.Core.Sessions;
using NovaDB.Protocol;

namespace NovaDB.Networking;

/// <summary>
/// Represents an active RESP client connection and its session state.
/// </summary>
public interface IClientConnection
{
    /// <summary>Gets the unique connection identifier.</summary>
    string ConnectionId { get; }

    /// <summary>
    /// Gets the remote peer address used for AUTH rate limiting (empty when unknown).
    /// </summary>
    string RemoteAddress { get; }

    /// <summary>Gets or sets whether the client has authenticated successfully.</summary>
    bool IsAuthenticated { get; set; }

    /// <summary>Gets or sets whether the client is in pub/sub mode.</summary>
    bool IsSubscribed { get; set; }

    /// <summary>
    /// Gets or sets whether the connection should close after the current response is written
    /// (for example after <c>QUIT</c>).
    /// </summary>
    bool ShouldClose { get; set; }

    /// <summary>Gets the mutable per-connection command session.</summary>
    ClientSession Session { get; }

    /// <summary>
    /// Requests an immediate connection teardown (cancels the read loop).
    /// </summary>
    void RequestClose();

    /// <summary>
    /// Writes a pushed RESP message to the client (for pub/sub notifications).
    /// </summary>
    /// <param name="message">The RESP value to push.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask PushMessageAsync(RespValue message, CancellationToken cancellationToken);
}
