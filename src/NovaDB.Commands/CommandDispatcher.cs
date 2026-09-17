using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Commands.Persistence;
using NovaDB.Commands.Security;
using NovaDB.Configuration;
using NovaDB.Core.Exceptions;
using NovaDB.Monitoring;
using NovaDB.Protocol;

namespace NovaDB.Commands;

/// <summary>
/// Routes parsed commands to registered handlers with auth, transaction, and pub/sub gates.
/// </summary>
public sealed class CommandDispatcher : ICommandExecutor
{
    private static readonly HashSet<string> TransactionCommands = new(StringComparer.Ordinal)
    {
        "MULTI", "EXEC", "DISCARD", "WATCH", "UNWATCH"
    };

    private static readonly HashSet<string> PubSubAllowedCommands = new(StringComparer.Ordinal)
    {
        "SUBSCRIBE", "UNSUBSCRIBE", "PING", "QUIT"
    };

    /// <summary>
    /// Commands whose effects must be written to the append-only log. Keep this in sync with every
    /// registered handler that changes stored state; a missing entry is silent data loss on restart.
    /// PUBLISH is deliberately absent because pub/sub delivery is not stored state.
    /// </summary>
    private static readonly HashSet<string> MutatingCommands = new(StringComparer.Ordinal)
    {
        "SET", "SETEX", "SETNX", "PSETEX", "MSET", "MSETNX", "DEL", "UNLINK", "GETDEL",
        "INCR", "DECR", "INCRBY", "DECRBY", "INCRBYFLOAT", "APPEND",
        "EXPIRE", "PEXPIREAT", "PERSIST", "GETEX",
        "HSET", "HDEL", "HINCRBY",
        "LPUSH", "RPUSH", "LPOP", "RPOP",
        "SADD", "SREM",
        "ZADD",
        "FLUSHDB", "FLUSHALL"
    };

    private readonly IReadOnlyDictionary<string, ICommandHandler> _handlers;
    private readonly ICommandMutationSink _mutationSink;
    private readonly IOptions<NovaDbOptions> _options;
    private readonly INovaDbMetrics _metrics;
    private readonly CommandAuthorization _authorization;
    private readonly ILogger<CommandDispatcher> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandDispatcher"/> class.
    /// </summary>
    public CommandDispatcher(
        IEnumerable<ICommandHandler> handlers,
        ICommandMutationSink mutationSink,
        IOptions<NovaDbOptions> options,
        INovaDbMetrics metrics,
        CommandAuthorization authorization,
        ILogger<CommandDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(mutationSink);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(logger);

        _handlers = handlers.ToDictionary(h => h.Name, StringComparer.Ordinal);
        _mutationSink = mutationSink;
        _options = options;
        _metrics = metrics;
        _authorization = authorization;
        _logger = logger;
    }

    /// <summary>
    /// Gets all registered command names.
    /// </summary>
    public IReadOnlyCollection<string> CommandNames => _handlers.Keys.ToArray();

    /// <summary>
    /// Dispatches a command through the full pipeline.
    /// </summary>
    /// <param name="context">Command context.</param>
    /// <returns>The RESP response.</returns>
    public async ValueTask<RespValue> DispatchAsync(CommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var commandName = context.CommandName;
        var isMutating = MutatingCommands.Contains(commandName);

        if (!_authorization.IsAllowed(context.Session.Role, commandName, isMutating))
        {
            return RespValue.Error(new AuthenticationException("Authentication required.").ToRespError());
        }

        if (_options.Value.AuthenticationRequired
            && !context.Session.IsAuthenticated
            && !string.Equals(commandName, "AUTH", StringComparison.Ordinal)
            && !string.Equals(commandName, "HELLO", StringComparison.Ordinal)
            && !string.Equals(commandName, "ACL", StringComparison.Ordinal)
            && !string.Equals(commandName, "COMMAND", StringComparison.Ordinal)
            && !string.Equals(commandName, "PING", StringComparison.Ordinal)
            && !string.Equals(commandName, "QUIT", StringComparison.Ordinal))
        {
            return RespValue.Error(new AuthenticationException("Authentication required.").ToRespError());
        }

        if (context.Session.IsPubSubMode && !PubSubAllowedCommands.Contains(commandName))
        {
            return RespValue.Error("ERR only (P|S)SUBSCRIBE / (P|S)UNSUBSCRIBE / PING / QUIT are allowed in this context");
        }

        if (context.Session.InMulti && !TransactionCommands.Contains(commandName))
        {
            context.Session.QueuedCommands.Add(RespValue.FromArray(context.Arguments));
            return RespValue.SimpleString("QUEUED");
        }

        return await ExecuteHandlerAsync(context, recordMutation: true).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteDirectAsync(CommandContext context, bool recordMutation = true)
        => ExecuteHandlerAsync(context, recordMutation);

    /// <summary>Returns whether <paramref name="commandName"/> is persisted to the AOF.</summary>
    internal static bool IsMutatingCommand(string commandName)
        => MutatingCommands.Contains(commandName);

    private async ValueTask<RespValue> ExecuteHandlerAsync(CommandContext context, bool recordMutation)
    {
        var commandName = context.CommandName;

        if (!_handlers.TryGetValue(commandName, out var handler))
        {
            return RespValue.Error($"ERR unknown command '{commandName}'");
        }

        if (_options.Value.ReadOnlyReplica
            && recordMutation
            && !context.IsReplay
            && MutatingCommands.Contains(commandName))
        {
            return RespValue.Error("READONLY You can't write against a read only replica.");
        }

        try
        {
            var started = Stopwatch.GetTimestamp();
            var response = await handler.ExecuteAsync(context).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            _metrics.RecordCommandLatency(commandName, elapsedMs);

            if (commandName is "GET" or "MGET")
            {
                RecordGetHitMiss(commandName, response);
            }

            if (recordMutation
                && !context.IsReplay
                && response.Type != RespType.Error
                && MutatingCommands.Contains(commandName)
                && !context.Session.InMulti)
            {
                var record = AofCommandRewriter.ToDurableForm(commandName, context.Arguments);
                var metadata = new CommandMutationMetadata(
                    context.Session.ConnectionId,
                    TransactionId: string.Empty,
                    Version: 0,
                    DedupeKey: null);
                await _mutationSink
                    .OnMutatingCommandAsync(record, metadata, context.CancellationToken)
                    .ConfigureAwait(false);
            }

            return response;
        }
        catch (NovaDbException ex)
        {
            _logger.LogDebug(ex, "Command {Command} failed for connection {ConnectionId}", commandName, context.Connection.ConnectionId);
            return RespValue.Error(ex.ToRespError());
        }
    }

    private void RecordGetHitMiss(string commandName, RespValue response)
    {
        if (commandName == "GET")
        {
            if (response.IsNullBulk)
            {
                _metrics.RecordCacheMiss();
            }
            else if (response.Type == RespType.BulkString)
            {
                _metrics.RecordCacheHit();
            }

            return;
        }

        if (response.Type != RespType.Array || response.Array is null)
        {
            return;
        }

        foreach (var item in response.Array)
        {
            if (item.IsNullBulk)
            {
                _metrics.RecordCacheMiss();
            }
            else
            {
                _metrics.RecordCacheHit();
            }
        }
    }
}
