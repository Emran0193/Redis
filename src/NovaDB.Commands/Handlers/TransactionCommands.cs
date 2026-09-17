using Microsoft.Extensions.DependencyInjection;
using NovaDB.Commands.Internal;
using NovaDB.Commands.Persistence;
using NovaDB.Core.Models;
using NovaDB.Protocol;
using NovaDB.Transactions;

namespace NovaDB.Commands.Handlers;

/// <summary>Handles the MULTI command.</summary>
public sealed class MultiCommandHandler : ICommandHandler
{
    private readonly ITransactionManager _transactionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiCommandHandler"/> class.
    /// </summary>
    public MultiCommandHandler(ITransactionManager transactionManager)
        => _transactionManager = transactionManager;

    /// <inheritdoc />
    public string Name => "MULTI";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 1);
        return await _transactionManager
            .MultiAsync(context.Session, context.CancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Handles the EXEC command.</summary>
public sealed class ExecCommandHandler : ICommandHandler
{
    private readonly ITransactionManager _transactionManager;
    private readonly ICommandMutationSink _mutationSink;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExecCommandHandler"/> class.
    /// </summary>
    public ExecCommandHandler(ITransactionManager transactionManager, ICommandMutationSink mutationSink)
    {
        _transactionManager = transactionManager;
        _mutationSink = mutationSink;
    }

    /// <inheritdoc />
    public string Name => "EXEC";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 1);
        var executor = context.Services.GetRequiredService<ICommandExecutor>();
        var aofBatch = new List<RespValue>();

        var result = await _transactionManager.ExecAsync(
            context.Session,
            async (queuedCommand, ct) =>
            {
                if (queuedCommand.Type != RespType.Array || queuedCommand.Array is null || queuedCommand.Array.Length == 0)
                {
                    return RespValue.Error("ERR invalid queued command");
                }

                var innerContext = new CommandContext
                {
                    Connection = context.Connection,
                    Session = context.Session,
                    Arguments = queuedCommand.Array,
                    Services = context.Services,
                    CancellationToken = ct
                };

                // Defer AOF until after the exclusive EXEC section so Always-fsync does not
                // extend the global exclusive hold for every subcommand.
                var response = await executor
                    .ExecuteDirectAsync(innerContext, recordMutation: false)
                    .ConfigureAwait(false);

                var commandName = innerContext.CommandName;
                if (response.Type != RespType.Error
                    && CommandDispatcher.IsMutatingCommand(commandName))
                {
                    aofBatch.Add(AofCommandRewriter.ToDurableForm(commandName, innerContext.Arguments));
                }

                return response;
            },
            context.CancellationToken).ConfigureAwait(false);

        // WATCH conflict → null bulk; nothing was applied.
        if (!result.IsNullBulk && aofBatch.Count > 0)
        {
            var txId = $"{context.Session.ConnectionId}:{DateTimeOffset.UtcNow.UtcTicks}";
            var metadata = new CommandMutationMetadata(
                context.Session.ConnectionId,
                txId,
                Version: 0,
                DedupeKey: txId);
            await _mutationSink
                .OnMutatingCommandBatchAsync(aofBatch, metadata, context.CancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }
}

/// <summary>Handles the DISCARD command.</summary>
public sealed class DiscardCommandHandler : ICommandHandler
{
    private readonly ITransactionManager _transactionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscardCommandHandler"/> class.
    /// </summary>
    public DiscardCommandHandler(ITransactionManager transactionManager)
        => _transactionManager = transactionManager;

    /// <inheritdoc />
    public string Name => "DISCARD";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 1);
        return await _transactionManager
            .DiscardAsync(context.Session, context.CancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Handles the WATCH command.</summary>
public sealed class WatchCommandHandler : ICommandHandler
{
    private readonly ITransactionManager _transactionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchCommandHandler"/> class.
    /// </summary>
    public WatchCommandHandler(ITransactionManager transactionManager)
        => _transactionManager = transactionManager;

    /// <inheritdoc />
    public string Name => "WATCH";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 2);

        var keys = new List<RedisKey>(context.Arguments.Length - 1);
        for (var i = 1; i < context.Arguments.Length; i++)
        {
            keys.Add(CommandArgumentReader.GetKey(context.Arguments[i], "key"));
        }

        return await _transactionManager
            .WatchAsync(context.Session, keys, context.CancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Handles the UNWATCH command.</summary>
public sealed class UnwatchCommandHandler : ICommandHandler
{
    private readonly ITransactionManager _transactionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnwatchCommandHandler"/> class.
    /// </summary>
    public UnwatchCommandHandler(ITransactionManager transactionManager)
        => _transactionManager = transactionManager;

    /// <inheritdoc />
    public string Name => "UNWATCH";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 1);
        return await _transactionManager
            .UnwatchAsync(context.Session, context.CancellationToken)
            .ConfigureAwait(false);
    }
}
