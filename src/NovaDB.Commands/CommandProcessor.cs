using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NovaDB.Commands.PubSub;
using NovaDB.Networking;
using NovaDB.Protocol;
using NovaDB.PubSub;

namespace NovaDB.Commands;

/// <summary>
/// Processes parsed RESP requests by dispatching to <see cref="CommandDispatcher"/>.
/// </summary>
public sealed class CommandProcessor : ICommandProcessor
{
    private readonly CommandDispatcher _dispatcher;
    private readonly IServiceProvider _services;
    private readonly PubSubSubscriberCache _subscriberCache;
    private readonly IPubSubHub _pubSubHub;
    private readonly AuthRateLimiter _authRateLimiter;
    private readonly ILogger<CommandProcessor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandProcessor"/> class.
    /// </summary>
    public CommandProcessor(
        CommandDispatcher dispatcher,
        IServiceProvider services,
        PubSubSubscriberCache subscriberCache,
        IPubSubHub pubSubHub,
        AuthRateLimiter authRateLimiter,
        ILogger<CommandProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(subscriberCache);
        ArgumentNullException.ThrowIfNull(pubSubHub);
        ArgumentNullException.ThrowIfNull(authRateLimiter);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _services = services;
        _subscriberCache = subscriberCache;
        _pubSubHub = pubSubHub;
        _authRateLimiter = authRateLimiter;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<RespValue> ProcessAsync(
        IClientConnection connection,
        RespValue request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Type != RespType.Array || request.Array is null || request.Array.Length == 0)
        {
            return RespValue.Error("ERR invalid command format");
        }

        var session = connection.Session;
        connection.IsSubscribed = session.IsPubSubMode;

        var context = new CommandContext
        {
            Connection = connection,
            Session = session,
            Arguments = request.Array,
            Services = _services,
            CancellationToken = cancellationToken
        };

        var response = await _dispatcher.DispatchAsync(context).ConfigureAwait(false);

        connection.IsSubscribed = session.IsPubSubMode;
        connection.ShouldClose = session.ShouldClose;

        if (session.ShouldClose)
        {
            _logger.LogDebug("Client {ConnectionId} requested QUIT", connection.ConnectionId);
        }

        return response;
    }

    /// <inheritdoc />
    public async ValueTask OnDisconnectedAsync(string connectionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        cancellationToken.ThrowIfCancellationRequested();

        _pubSubHub.RemoveSubscriber(connectionId);
        _authRateLimiter.Forget(connectionId);

        var subscriber = _subscriberCache.TryRemove(connectionId);
        if (subscriber is not null)
        {
            await subscriber.DisposeAsync().ConfigureAwait(false);
        }

        _logger.LogDebug("Released command-layer state for disconnected client {ConnectionId}", connectionId);
    }
}
