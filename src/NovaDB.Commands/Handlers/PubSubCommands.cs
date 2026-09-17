using NovaDB.Commands.Internal;
using NovaDB.Commands.PubSub;
using NovaDB.Protocol;
using NovaDB.PubSub;

namespace NovaDB.Commands.Handlers;

/// <summary>Handles the SUBSCRIBE command.</summary>
public sealed class SubscribeCommandHandler : ICommandHandler
{
    private readonly IPubSubHub _pubSubHub;
    private readonly PubSubSubscriberCache _subscriberCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="SubscribeCommandHandler"/> class.
    /// </summary>
    public SubscribeCommandHandler(IPubSubHub pubSubHub, PubSubSubscriberCache subscriberCache)
    {
        _pubSubHub = pubSubHub;
        _subscriberCache = subscriberCache;
    }

    /// <inheritdoc />
    public string Name => "SUBSCRIBE";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 2);

        var channels = new List<string>(context.Arguments.Length - 1);
        for (var i = 1; i < context.Arguments.Length; i++)
        {
            channels.Add(context.Arguments[i].AsUtf8String());
        }

        var subscriber = _subscriberCache.GetOrAdd(context.Connection);
        var totalSubscriptions = await _pubSubHub
            .SubscribeAsync(subscriber, channels, context.CancellationToken)
            .ConfigureAwait(false);

        RespValue? firstResponse = null;
        for (var i = 0; i < channels.Count; i++)
        {
            context.Session.Subscriptions.Add(channels[i]);
            var ack = RespValue.FromArray(
            [
                RespValue.BulkString("subscribe"),
                RespValue.BulkString(channels[i]),
                RespValue.FromInteger(totalSubscriptions - channels.Count + i + 1)
            ]);

            if (i == 0)
            {
                firstResponse = ack;
            }
            else
            {
                await context.Connection.PushMessageAsync(ack, context.CancellationToken).ConfigureAwait(false);
            }
        }

        return firstResponse ?? RespValue.FromArray(
        [
            RespValue.BulkString("subscribe"),
            RespValue.NullBulk(),
            RespValue.FromInteger(totalSubscriptions)
        ]);
    }
}

/// <summary>Handles the UNSUBSCRIBE command.</summary>
public sealed class UnsubscribeCommandHandler : ICommandHandler
{
    private readonly IPubSubHub _pubSubHub;
    private readonly PubSubSubscriberCache _subscriberCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnsubscribeCommandHandler"/> class.
    /// </summary>
    public UnsubscribeCommandHandler(IPubSubHub pubSubHub, PubSubSubscriberCache subscriberCache)
    {
        _pubSubHub = pubSubHub;
        _subscriberCache = subscriberCache;
    }

    /// <inheritdoc />
    public string Name => "UNSUBSCRIBE";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        var channels = new List<string>();
        for (var i = 1; i < context.Arguments.Length; i++)
        {
            channels.Add(context.Arguments[i].AsUtf8String());
        }

        var subscriber = _subscriberCache.GetOrAdd(context.Connection);
        var remaining = await _pubSubHub
            .UnsubscribeAsync(subscriber, channels, context.CancellationToken)
            .ConfigureAwait(false);

        if (channels.Count == 0)
        {
            context.Session.Subscriptions.Clear();
            return RespValue.FromArray(
            [
                RespValue.BulkString("unsubscribe"),
                RespValue.NullBulk(),
                RespValue.FromInteger(remaining)
            ]);
        }

        RespValue? firstResponse = null;
        for (var i = 0; i < channels.Count; i++)
        {
            context.Session.Subscriptions.Remove(channels[i]);
            var ack = RespValue.FromArray(
            [
                RespValue.BulkString("unsubscribe"),
                RespValue.BulkString(channels[i]),
                RespValue.FromInteger(Math.Max(0, remaining))
            ]);

            if (i == 0)
            {
                firstResponse = ack;
            }
            else
            {
                await context.Connection.PushMessageAsync(ack, context.CancellationToken).ConfigureAwait(false);
            }
        }

        return firstResponse ?? RespValue.FromArray(
        [
            RespValue.BulkString("unsubscribe"),
            RespValue.NullBulk(),
            RespValue.FromInteger(remaining)
        ]);
    }
}

/// <summary>Handles the PUBLISH command.</summary>
public sealed class PublishCommandHandler : ICommandHandler
{
    private readonly IPubSubHub _pubSubHub;

    /// <summary>
    /// Initializes a new instance of the <see cref="PublishCommandHandler"/> class.
    /// </summary>
    public PublishCommandHandler(IPubSubHub pubSubHub) => _pubSubHub = pubSubHub;

    /// <inheritdoc />
    public string Name => "PUBLISH";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 3);

        var channel = context.Arguments[1].AsUtf8String();
        var message = CommandArgumentReader.GetBulkBytes(context.Arguments[2], "message");
        var receivers = await _pubSubHub
            .PublishAsync(channel, message, context.CancellationToken)
            .ConfigureAwait(false);

        return RespValue.FromInteger(receivers);
    }
}
