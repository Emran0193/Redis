using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NovaDB.Core.Models;
using NovaDB.Core.Sessions;
using NovaDB.Protocol;
using NovaDB.Storage;

namespace NovaDB.Transactions;

/// <summary>
/// Manages MULTI/EXEC/DISCARD/WATCH optimistic transactions.
/// </summary>
public interface ITransactionManager
{
    /// <summary>Begins a MULTI transaction.</summary>
    ValueTask<RespValue> MultiAsync(ClientSession session, CancellationToken cancellationToken);

    /// <summary>Discards a MULTI transaction.</summary>
    ValueTask<RespValue> DiscardAsync(ClientSession session, CancellationToken cancellationToken);

    /// <summary>Watches keys for optimistic concurrency.</summary>
    ValueTask<RespValue> WatchAsync(ClientSession session, IReadOnlyList<RedisKey> keys, CancellationToken cancellationToken);

    /// <summary>Clears all watched keys for the session.</summary>
    ValueTask<RespValue> UnwatchAsync(ClientSession session, CancellationToken cancellationToken);

    /// <summary>Executes queued commands when watched keys are unchanged.</summary>
    ValueTask<RespValue> ExecAsync(
        ClientSession session,
        Func<RespValue, CancellationToken, ValueTask<RespValue>> executeQueued,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default optimistic transaction manager.
/// </summary>
public sealed class TransactionManager : ITransactionManager
{
    private readonly IStorageEngine _storage;
    private readonly ILogger<TransactionManager> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TransactionManager"/> class.
    /// </summary>
    public TransactionManager(IStorageEngine storage, ILogger<TransactionManager> logger)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public ValueTask<RespValue> MultiAsync(ClientSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        if (session.InMulti)
        {
            return ValueTask.FromResult(RespValue.Error("ERR MULTI calls can not be nested"));
        }

        session.InMulti = true;
        session.QueuedCommands.Clear();
        return ValueTask.FromResult(RespValue.Ok);
    }

    /// <inheritdoc />
    public ValueTask<RespValue> DiscardAsync(ClientSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        if (!session.InMulti)
        {
            return ValueTask.FromResult(RespValue.Error("ERR DISCARD without MULTI"));
        }

        session.ClearTransaction();
        return ValueTask.FromResult(RespValue.Ok);
    }

    /// <inheritdoc />
    /// <summary>
    /// Watches keys for optimistic concurrency using binary key identity.
    /// </summary>
    public async ValueTask<RespValue> WatchAsync(
        ClientSession session,
        IReadOnlyList<RedisKey> keys,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(keys);

        if (session.InMulti)
        {
            return RespValue.Error("ERR WATCH inside MULTI is not allowed");
        }

        for (var i = 0; i < keys.Count; i++)
        {
            var version = await _storage.GetKeyVersionAsync(keys[i], cancellationToken).ConfigureAwait(false);
            session.WatchedKeyVersions[keys[i]] = version;
        }

        return RespValue.Ok;
    }

    /// <inheritdoc />
    public ValueTask<RespValue> UnwatchAsync(ClientSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        session.ClearWatches();
        return ValueTask.FromResult(RespValue.Ok);
    }

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecAsync(
        ClientSession session,
        Func<RespValue, CancellationToken, ValueTask<RespValue>> executeQueued,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(executeQueued);

        if (!session.InMulti)
        {
            return RespValue.Error("ERR EXEC without MULTI");
        }

        return await _storage.RunExclusiveAsync(
            async ct =>
            {
                foreach (var pair in session.WatchedKeyVersions)
                {
                    var current = await _storage.GetKeyVersionAsync(pair.Key, ct).ConfigureAwait(false);
                    if (current != pair.Value)
                    {
                        _logger.LogDebug("EXEC aborted due to WATCH conflict on {ConnectionId}", session.ConnectionId);
                        session.ClearTransaction();
                        return RespValue.NullBulk();
                    }
                }

                var queue = new RespValue[session.QueuedCommands.Count];
                for (var i = 0; i < session.QueuedCommands.Count; i++)
                {
                    if (session.QueuedCommands[i] is not RespValue command)
                    {
                        session.ClearTransaction();
                        return RespValue.Error("ERR invalid queued command");
                    }

                    queue[i] = command;
                }

                session.InMulti = false;
                session.QueuedCommands.Clear();
                session.WatchedKeyVersions.Clear();

                var results = new RespValue[queue.Length];
                for (var i = 0; i < queue.Length; i++)
                {
                    results[i] = await executeQueued(queue[i], ct).ConfigureAwait(false);
                }

                return RespValue.FromArray(results);
            },
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// DI registration for transactions.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers transaction services.
    /// </summary>
    public static IServiceCollection AddNovaDbTransactions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ITransactionManager, TransactionManager>();
        return services;
    }
}
