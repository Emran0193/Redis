using NovaDB.Core.Sessions;
using NovaDB.Networking;
using NovaDB.Protocol;

namespace UnitTests.Helpers;

/// <summary>
/// In-memory <see cref="IClientConnection"/> for command handler tests without TCP.
/// </summary>
public sealed class FakeClientConnection : IClientConnection
{
    /// <summary>
    /// Initializes a new fake connection.
    /// </summary>
    public FakeClientConnection(string? connectionId = null, string? remoteAddress = null)
    {
        ConnectionId = connectionId ?? Guid.NewGuid().ToString("N");
        RemoteAddress = remoteAddress ?? "127.0.0.1";
        Session = new ClientSession(ConnectionId) { IsAuthenticated = true };
    }

    /// <inheritdoc />
    public string ConnectionId { get; }

    /// <inheritdoc />
    public string RemoteAddress { get; set; }

    /// <inheritdoc />
    public bool IsAuthenticated
    {
        get => Session.IsAuthenticated;
        set => Session.IsAuthenticated = value;
    }

    /// <inheritdoc />
    public bool IsSubscribed { get; set; }

    /// <inheritdoc />
    public bool ShouldClose { get; set; }

    /// <summary>Counts <see cref="RequestClose"/> invocations.</summary>
    public int CloseRequests { get; private set; }

    /// <inheritdoc />
    public ClientSession Session { get; }

    /// <summary>Gets pushed pub/sub messages.</summary>
    public List<RespValue> PushedMessages { get; } = [];

    /// <inheritdoc />
    public void RequestClose()
    {
        ShouldClose = true;
        CloseRequests++;
    }

    /// <inheritdoc />
    public ValueTask PushMessageAsync(RespValue message, CancellationToken cancellationToken)
    {
        PushedMessages.Add(message);
        return ValueTask.CompletedTask;
    }
}
