using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NovaDB.Networking;

namespace NovaDB.Commands.PubSub;

/// <summary>
/// Caches one <see cref="ConnectionPubSubSubscriber"/> per connection for hub identity.
/// </summary>
public sealed class PubSubSubscriberCache
{
    private readonly ConcurrentDictionary<string, ConnectionPubSubSubscriber> _subscribers =
        new(StringComparer.Ordinal);
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="PubSubSubscriberCache"/> class.
    /// </summary>
    /// <param name="loggerFactory">Logger factory used for per-subscriber pumps.</param>
    public PubSubSubscriberCache(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <summary>
    /// Gets or creates the pub/sub subscriber adapter for a connection.
    /// </summary>
    /// <param name="connection">Client connection.</param>
    /// <returns>Stable subscriber instance for the connection.</returns>
    public ConnectionPubSubSubscriber GetOrAdd(IClientConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return _subscribers.GetOrAdd(
            connection.ConnectionId,
            _ => new ConnectionPubSubSubscriber(
                connection,
                _loggerFactory.CreateLogger<ConnectionPubSubSubscriber>()));
    }

    /// <summary>
    /// Removes and disposes the subscriber for a connection, if any.
    /// </summary>
    /// <param name="connectionId">Connection identifier.</param>
    /// <returns>The removed subscriber, or <see langword="null"/>.</returns>
    public ConnectionPubSubSubscriber? TryRemove(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return _subscribers.TryRemove(connectionId, out var subscriber) ? subscriber : null;
    }
}
