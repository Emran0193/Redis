using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NovaDB.Commands.Internal;
using NovaDB.Configuration;
using NovaDB.Core.Exceptions;
using NovaDB.Networking;
using NovaDB.Protocol;
using NovaDB.Storage;
using NovaDB.Storage.Eviction;

namespace NovaDB.Commands.Handlers;

/// <summary>Handles HELLO — RESP handshake. NovaDB speaks RESP2 only.</summary>
public sealed class HelloCommandHandler : ICommandHandler
{
    private readonly AuthPasswordVerifier _verifier;

    /// <summary>
    /// Initializes a new instance of the <see cref="HelloCommandHandler"/> class.
    /// </summary>
    public HelloCommandHandler(AuthPasswordVerifier verifier) => _verifier = verifier;

    /// <inheritdoc />
    public string Name => "HELLO";

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        // HELLO [protover] [AUTH user pass] [SETNAME name]
        var proto = 2;
        if (context.Arguments.Length >= 2)
        {
            proto = (int)CommandArgumentReader.GetInteger(context.Arguments[1], "protover");
        }

        if (proto is not (2 or 3))
        {
            return ValueTask.FromResult(RespValue.Error("NOPROTO unsupported protocol version"));
        }

        if (proto == 3)
        {
            // StackExchange.Redis and others probe RESP3; refuse clearly so they fall back to RESP2.
            return ValueTask.FromResult(RespValue.Error("NOPROTO sorry this protocol version is not supported"));
        }

        for (var i = 2; i < context.Arguments.Length; i++)
        {
            var option = context.Arguments[i].AsUtf8String().ToUpperInvariant();
            if (option == "AUTH")
            {
                // user + password; NovaDB has a single password and ignores the username.
                if (i + 2 >= context.Arguments.Length)
                {
                    throw new SyntaxException("syntax error");
                }

                var password = context.Arguments[i + 2].AsUtf8String();
                i += 2;
                _verifier.AuthenticateOrThrow(context.Connection.ConnectionId, password, context.Connection.RemoteAddress);
                context.Session.IsAuthenticated = true;
                context.Session.Role = Security.CommandAuthorization.RoleAfterAuth();
                context.Connection.IsAuthenticated = true;
            }
            else if (option == "SETNAME")
            {
                if (i + 1 >= context.Arguments.Length)
                {
                    throw new SyntaxException("syntax error");
                }

                context.Session.ClientName = context.Arguments[++i].AsUtf8String();
            }
            else
            {
                throw new SyntaxException($"syntax error, unexpected '{option}'");
            }
        }

        // RESP2 map as a flat array of field/value pairs (Redis HELLO reply shape).
        var reply = RespValue.FromArray(
        [
            RespValue.BulkString("server"), RespValue.BulkString("novadb"),
            RespValue.BulkString("version"), RespValue.BulkString("1.0.0"),
            RespValue.BulkString("proto"), RespValue.FromInteger(2),
            RespValue.BulkString("id"), RespValue.FromInteger(1),
            RespValue.BulkString("mode"), RespValue.BulkString("standalone"),
            RespValue.BulkString("role"), RespValue.BulkString("master"),
            RespValue.BulkString("modules"), RespValue.FromArray([])
        ]);

        return ValueTask.FromResult(reply);
    }
}

/// <summary>Handles SELECT — only database 0 is supported.</summary>
public sealed class SelectCommandHandler : ICommandHandler
{
    /// <inheritdoc />
    public string Name => "SELECT";

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 2);
        var index = CommandArgumentReader.GetInteger(context.Arguments[1], "index");
        if (index != 0)
        {
            return ValueTask.FromResult(RespValue.Error("ERR DB index is out of range"));
        }

        return ValueTask.FromResult(RespValue.Ok);
    }
}

/// <summary>Handles CONFIG GET/SET for live maxmemory settings.</summary>
public sealed class ConfigCommandHandler : ICommandHandler
{
    private readonly IOptions<NovaDbOptions> _options;
    private readonly MemoryStorageEngine _storage;
    private readonly ConfigurableEvictionPolicy _eviction;
    private readonly Security.IAuditTrail? _audit;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigCommandHandler"/> class.
    /// </summary>
    public ConfigCommandHandler(
        IOptions<NovaDbOptions> options,
        MemoryStorageEngine storage,
        ConfigurableEvictionPolicy eviction,
        Security.IAuditTrail? audit = null)
    {
        _options = options;
        _storage = storage;
        _eviction = eviction;
        _audit = audit;
    }

    /// <inheritdoc />
    public string Name => "CONFIG";

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 2);
        var sub = context.Arguments[1].AsUtf8String().ToUpperInvariant();

        if (sub == "GET")
        {
            CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 3);
            var pattern = context.Arguments[2].AsUtf8String();
            var results = new List<RespValue>();

            void MaybeAdd(string name, string value)
            {
                if (pattern is "*" || name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(RespValue.BulkString(name));
                    results.Add(RespValue.BulkString(value));
                }
            }

            var o = _options.Value;
            MaybeAdd("maxmemory", _storage.MemoryLimitBytes.ToString(CultureInfo.InvariantCulture));
            MaybeAdd("maxmemory-policy", _eviction.PolicyName);
            MaybeAdd("appendonly", o.AofEnabled ? "yes" : "no");
            MaybeAdd("appendfsync", o.AofFlushPolicy);
            MaybeAdd("save", string.Empty);
            MaybeAdd("databases", "1");

            return ValueTask.FromResult(RespValue.FromArray(results.ToArray()));
        }

        if (sub == "SET")
        {
            CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 4);
            var key = context.Arguments[2].AsUtf8String().ToLowerInvariant();
            var value = context.Arguments[3].AsUtf8String();

            switch (key)
            {
                case "maxmemory":
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit)
                        || limit < 0)
                    {
                        return ValueTask.FromResult(RespValue.Error("ERR invalid maxmemory value"));
                    }

                    _storage.SetMemoryLimitBytes(limit);
                    _options.Value.MemoryLimitBytes = limit;
                    _audit?.Record(context.Session.ConnectionId, "CONFIG SET", $"maxmemory={limit}");
                    return ValueTask.FromResult(RespValue.Ok);

                case "maxmemory-policy":
                    if (!EvictionPolicyFactory.IsKnown(value))
                    {
                        return ValueTask.FromResult(
                            RespValue.Error("ERR Unsupported maxmemory-policy"));
                    }

                    _eviction.Replace(value);
                    _options.Value.EvictionPolicy = value;
                    _audit?.Record(context.Session.ConnectionId, "CONFIG SET", $"maxmemory-policy={value}");
                    return ValueTask.FromResult(RespValue.Ok);

                default:
                    // Tolerate unknown knobs clients set during connect.
                    return ValueTask.FromResult(RespValue.Ok);
            }
        }

        if (sub == "RESETSTAT")
        {
            return ValueTask.FromResult(RespValue.Ok);
        }

        return ValueTask.FromResult(RespValue.Error($"ERR unknown subcommand '{sub}'. Try CONFIG GET."));
    }
}

/// <summary>Handles CLIENT SETNAME / SETINFO / GETNAME / ID / INFO / LIST.</summary>
public sealed class ClientCommandHandler : ICommandHandler
{
    private readonly ConnectionManager? _connections;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClientCommandHandler"/> class.
    /// </summary>
    public ClientCommandHandler(IServiceProvider services)
        => _connections = services.GetService<ConnectionManager>();

    /// <inheritdoc />
    public string Name => "CLIENT";

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 2);
        var sub = context.Arguments[1].AsUtf8String().ToUpperInvariant();

        switch (sub)
        {
            case "SETNAME":
                CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 3);
                context.Session.ClientName = context.Arguments[2].AsUtf8String();
                return ValueTask.FromResult(RespValue.Ok);

            case "GETNAME":
                return ValueTask.FromResult(
                    context.Session.ClientName is null
                        ? RespValue.NullBulk()
                        : RespValue.BulkString(context.Session.ClientName));

            case "SETINFO":
                CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 4);
                var attr = context.Arguments[2].AsUtf8String().ToUpperInvariant();
                var value = context.Arguments[3].AsUtf8String();
                if (attr is "LIB-NAME")
                {
                    context.Session.LibraryName = value;
                }
                else if (attr is "LIB-VER")
                {
                    context.Session.LibraryVersion = value;
                }

                return ValueTask.FromResult(RespValue.Ok);

            case "ID":
                if (!long.TryParse(
                        context.Connection.ConnectionId,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var clientId))
                {
                    clientId = (long)(uint)context.Connection.ConnectionId.GetHashCode();
                }

                return ValueTask.FromResult(RespValue.FromInteger(clientId));

            case "INFO":
                var info =
                    $"id={context.Connection.ConnectionId} addr={context.Connection.RemoteAddress} " +
                    $"name={context.Session.ClientName ?? string.Empty} " +
                    $"lib-name={context.Session.LibraryName ?? string.Empty} " +
                    $"lib-ver={context.Session.LibraryVersion ?? string.Empty}";
                return ValueTask.FromResult(RespValue.BulkString(info));

            case "LIST":
                if (_connections is null)
                {
                    return ValueTask.FromResult(RespValue.BulkString(string.Empty));
                }

                var sb = new StringBuilder();
                foreach (var connection in _connections.GetConnections())
                {
                    sb.Append("id=").Append(connection.ConnectionId)
                        .Append(" addr=").Append(connection.RemoteAddress)
                        .Append(" name=").Append(connection.Session.ClientName ?? string.Empty)
                        .Append(" flags=").Append(connection.IsSubscribed ? 'P' : 'N')
                        .Append('\n');
                }

                return ValueTask.FromResult(RespValue.BulkString(sb.ToString()));

            default:
                return ValueTask.FromResult(RespValue.Error($"ERR unknown subcommand '{sub}'."));
        }
    }
}
