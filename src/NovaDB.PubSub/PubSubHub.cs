using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NovaDB.Protocol;

namespace NovaDB.PubSub;

/// <summary>
/// A subscriber capable of receiving pushed pub/sub messages.
/// </summary>
public interface IPubSubSubscriber
{
    /// <summary>Gets the connection identifier.</summary>
    string ConnectionId { get; }

    /// <summary>
    /// Gets a value indicating whether the subscriber's outbox overflowed and should be dropped.
    /// </summary>
    bool IsOverflowed => false;

    /// <summary>
    /// Sends a pushed message to the subscriber.
    /// </summary>
    ValueTask SendAsync(RespValue message, CancellationToken cancellationToken);
}

/// <summary>
/// Pub/sub hub for channel subscribe/publish fan-out.
/// </summary>
public interface IPubSubHub
{
    /// <summary>Subscribes a client to channels.</summary>
    ValueTask<long> SubscribeAsync(IPubSubSubscriber subscriber, IReadOnlyList<string> channels, CancellationToken cancellationToken);

    /// <summary>Unsubscribes a client from channels.</summary>
    ValueTask<long> UnsubscribeAsync(IPubSubSubscriber subscriber, IReadOnlyList<string> channels, CancellationToken cancellationToken);

    /// <summary>Publishes a message to a channel.</summary>
    ValueTask<long> PublishAsync(string channel, byte[] message, CancellationToken cancellationToken);

    /// <summary>Gets the number of channels that currently have at least one subscriber.</summary>
    int TrackedChannelCount { get; }

    /// <summary>
    /// Removes a subscriber from every channel. Called on disconnect so dead clients stop receiving fan-out.
    /// </summary>
    /// <param name="connectionId">Disconnecting connection identifier.</param>
    void RemoveSubscriber(string connectionId);
}

/// <summary>
/// Thread-safe async pub/sub hub.
/// </summary>
public sealed class PubSubHub : IPubSubHub
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, IPubSubSubscriber>> _channels =
        new(StringComparer.Ordinal);

    private readonly ILogger<PubSubHub> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PubSubHub"/> class.
    /// </summary>
    public PubSubHub(ILogger<PubSubHub> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public int TrackedChannelCount => _channels.Count;

    /// <inheritdoc />
    public ValueTask<long> SubscribeAsync(
        IPubSubSubscriber subscriber,
        IReadOnlyList<string> channels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        ArgumentNullException.ThrowIfNull(channels);
        cancellationToken.ThrowIfCancellationRequested();

        long total = 0;
        for (var i = 0; i < channels.Count; i++)
        {
            var channel = channels[i];
            ArgumentException.ThrowIfNullOrEmpty(channel);
            var subscribers = _channels.GetOrAdd(channel, static _ => new ConcurrentDictionary<string, IPubSubSubscriber>(StringComparer.Ordinal));
            subscribers[subscriber.ConnectionId] = subscriber;
            total = CountSubscriptions(subscriber.ConnectionId);
        }

        return ValueTask.FromResult(total);
    }

    /// <inheritdoc />
    public ValueTask<long> UnsubscribeAsync(
        IPubSubSubscriber subscriber,
        IReadOnlyList<string> channels,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        ArgumentNullException.ThrowIfNull(channels);
        cancellationToken.ThrowIfCancellationRequested();

        if (channels.Count == 0)
        {
            RemoveSubscriber(subscriber.ConnectionId);
        }
        else
        {
            for (var i = 0; i < channels.Count; i++)
            {
                if (_channels.TryGetValue(channels[i], out var subscribers))
                {
                    subscribers.TryRemove(subscriber.ConnectionId, out _);
                    if (subscribers.IsEmpty)
                    {
                        _channels.TryRemove(channels[i], out _);
                    }
                }
            }
        }

        return ValueTask.FromResult(CountSubscriptions(subscriber.ConnectionId));
    }

    /// <inheritdoc />
    public void RemoveSubscriber(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        foreach (var pair in _channels)
        {
            if (pair.Value.TryRemove(connectionId, out _) && pair.Value.IsEmpty)
            {
                _channels.TryRemove(pair.Key, out _);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask<long> PublishAsync(string channel, byte[] message, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(channel);
        ArgumentNullException.ThrowIfNull(message);

        if (!_channels.TryGetValue(channel, out var subscribers) || subscribers.IsEmpty)
        {
            return 0;
        }

        var payload = RespValue.FromArray(
        [
            RespValue.BulkString("message"),
            RespValue.BulkString(channel),
            RespValue.BulkString(message)
        ]);

        var snapshot = subscribers.Values.ToArray();
        var delivered = 0;

        // Fan-out is non-blocking at the hub: each subscriber owns a bounded outbox and returns
        // immediately from SendAsync. Task.WhenAll here therefore finishes promptly even when a
        // client is slow; overflowed subscribers are dropped after the loop.
        var tasks = new Task[snapshot.Length];
        for (var i = 0; i < snapshot.Length; i++)
        {
            tasks[i] = SendSafeAsync(snapshot[i], payload, cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        for (var i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i].IsOverflowed)
            {
                RemoveSubscriber(snapshot[i].ConnectionId);
                _logger.LogInformation("Dropped slow pub/sub subscriber {ConnectionId}", snapshot[i].ConnectionId);
            }
            else
            {
                delivered++;
            }
        }

        return delivered;
    }

    private async Task SendSafeAsync(IPubSubSubscriber subscriber, RespValue message, CancellationToken cancellationToken)
    {
        try
        {
            await subscriber.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fan-out pub/sub message to {ConnectionId}", subscriber.ConnectionId);
            RemoveSubscriber(subscriber.ConnectionId);
        }
    }

    private long CountSubscriptions(string connectionId)
    {
        long count = 0;
        foreach (var pair in _channels)
        {
            if (pair.Value.ContainsKey(connectionId))
            {
                count++;
            }
        }

        return count;
    }
}

/// <summary>
/// DI registration for pub/sub.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers pub/sub services.
    /// </summary>
    public static IServiceCollection AddNovaDbPubSub(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IPubSubHub, PubSubHub>();
        return services;
    }
}
