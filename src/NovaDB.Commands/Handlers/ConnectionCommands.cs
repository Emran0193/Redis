using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Commands.Internal;
using NovaDB.Configuration;
using NovaDB.Core.Exceptions;
using NovaDB.Monitoring;
using NovaDB.Protocol;
using NovaDB.Storage;

namespace NovaDB.Commands.Handlers;

/// <summary>Handles the PING command.</summary>
public sealed class PingCommandHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Name => "PING";

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Length == 1)
        {
            return ValueTask.FromResult(RespValue.Pong);
        }

        if (context.Arguments.Length == 2)
        {
            var message = CommandArgumentReader.GetBulkBytes(context.Arguments[1], "message");
            return ValueTask.FromResult(RespValue.BulkString(message));
        }

        throw SyntaxException.WrongNumberOfArguments(Name);
    }
}

/// <summary>Handles the ECHO command.</summary>
public sealed class EchoCommandHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Name => "ECHO";

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 2);
        var message = CommandArgumentReader.GetBulkBytes(context.Arguments[1], "message");
        return ValueTask.FromResult(RespValue.BulkString(message));
    }
}

/// <summary>Handles the QUIT command.</summary>
public sealed class QuitCommandHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Name => "QUIT";

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        context.Session.ShouldClose = true;
        return ValueTask.FromResult(RespValue.Ok);
    }
}

/// <summary>Handles the AUTH command.</summary>
public sealed class AuthCommandHandler : ICommandHandler
{
    private readonly AuthPasswordVerifier _verifier;
    private readonly ILogger<AuthCommandHandler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthCommandHandler"/> class.
    /// </summary>
    public AuthCommandHandler(AuthPasswordVerifier verifier, ILogger<AuthCommandHandler> logger)
    {
        _verifier = verifier;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "AUTH";

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 2);

        var password = context.Arguments.Length == 2
            ? context.Arguments[1].AsUtf8String()
            : context.Arguments[2].AsUtf8String();

        _verifier.AuthenticateOrThrow(context.Connection.ConnectionId, password, context.Connection.RemoteAddress);
        context.Session.IsAuthenticated = true;
        context.Connection.IsAuthenticated = true;
        _logger.LogDebug("Client {ConnectionId} authenticated", context.Connection.ConnectionId);
        return ValueTask.FromResult(RespValue.Ok);
    }
}

/// <summary>Handles the COMMAND command.</summary>
public sealed class CommandCommandHandler : ICommandHandler
{
    private readonly IServiceProvider _services;

    /// <summary>
    /// Initializes a new instance of the <see cref="CommandCommandHandler"/> class.
    /// </summary>
    public CommandCommandHandler(IServiceProvider services)
    {
        _services = services;
    }

    /// <inheritdoc />
    public string Name => "COMMAND";

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Length == 1)
        {
            var names = _services.GetServices<ICommandHandler>()
                .Select(static h => h.Name)
                .OrderBy(static n => n, StringComparer.Ordinal)
                .Select(static n => RespValue.BulkString(n))
                .ToArray();
            return ValueTask.FromResult(RespValue.FromArray(names));
        }

        if (context.Arguments.Length >= 2
            && string.Equals(context.Arguments[1].AsUtf8String(), "INFO", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(RespValue.FromArray([]));
        }

        return ValueTask.FromResult(RespValue.FromArray([]));
    }
}

/// <summary>Handles the INFO command.</summary>
public sealed class InfoCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;
    private readonly IOptions<NovaDbOptions> _options;
    private readonly INovaDbMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="InfoCommandHandler"/> class.
    /// </summary>
    public InfoCommandHandler(
        IStorageEngine storage,
        IOptions<NovaDbOptions> options,
        INovaDbMetrics metrics)
    {
        _storage = storage;
        _options = options;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public string Name => "INFO";

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        var snapshot = _metrics.GetSnapshot();
        var options = _options.Value;
        var info = $"""
            # Server
            novadb_version:1.0.0
            tcp_port:{options.Port}
            # Clients
            connected_clients:{snapshot.ConnectedClients}
            # Memory
            used_memory:{_storage.EstimatedMemoryBytes}
            maxmemory:{options.MemoryLimitBytes}
            maxmemory_policy:{options.EvictionPolicy}
            # Stats
            keyspace_hits:{snapshot.CacheHits}
            keyspace_misses:{snapshot.CacheMisses}
            evicted_keys:{snapshot.Evictions}
            expired_keys:{snapshot.ExpiredKeys}
            # Keyspace
            db0:keys={_storage.KeyCount},expires=0,avg_ttl=0
            # Persistence
            aof_enabled:{(options.AofEnabled ? 1 : 0)}
            # Replication
            role:master
            connected_slaves:0
            """;

        return ValueTask.FromResult(RespValue.BulkString(info.Replace("\r\n", "\n", StringComparison.Ordinal)));
    }
}
