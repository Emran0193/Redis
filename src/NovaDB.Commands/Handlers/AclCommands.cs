using NovaDB.Commands.Internal;
using NovaDB.Configuration;
using NovaDB.Protocol;
using Microsoft.Extensions.Options;

namespace NovaDB.Commands.Handlers;

/// <summary>
/// Minimal ACL surface for Redis clients that probe ACL WHOAMI/LIST.
/// Full user ACL is not implemented — NovaDB uses a single password (AUTH).
/// </summary>
public sealed class AclCommandHandler : ICommandHandler
{
    private readonly IOptions<NovaDbOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="AclCommandHandler"/> class.
    /// </summary>
    public AclCommandHandler(IOptions<NovaDbOptions> options) => _options = options;

    /// <inheritdoc />
    public string Name => "ACL";

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 2);
        var sub = context.Arguments[1].AsUtf8String().ToUpperInvariant();

        switch (sub)
        {
            case "WHOAMI":
                return ValueTask.FromResult(RespValue.BulkString("default"));

            case "LIST":
                var password = _options.Value.Password;
                var entry = string.IsNullOrEmpty(password)
                    ? "user default on nopass ~* &* +@all"
                    : "user default on #hash ~* &* +@all";
                return ValueTask.FromResult(RespValue.FromArray([RespValue.BulkString(entry)]));

            case "USERS":
                return ValueTask.FromResult(RespValue.FromArray([RespValue.BulkString("default")]));

            case "GETUSER":
                return ValueTask.FromResult(RespValue.FromArray([
                    RespValue.BulkString("flags"),
                    RespValue.FromArray([RespValue.BulkString("on")]),
                    RespValue.BulkString("passwords"),
                    RespValue.FromArray([]),
                    RespValue.BulkString("commands"),
                    RespValue.BulkString("+@all"),
                    RespValue.BulkString("keys"),
                    RespValue.FromArray([RespValue.BulkString("~*")]),
                    RespValue.BulkString("channels"),
                    RespValue.FromArray([RespValue.BulkString("&*")])
                ]));

            case "CAT":
                return ValueTask.FromResult(RespValue.FromArray([
                    RespValue.BulkString("keyspace"),
                    RespValue.BulkString("read"),
                    RespValue.BulkString("write"),
                    RespValue.BulkString("string"),
                    RespValue.BulkString("connection"),
                    RespValue.BulkString("admin")
                ]));

            case "HELP":
                return ValueTask.FromResult(RespValue.FromArray([
                    RespValue.BulkString("ACL WHOAMI -- return the current username"),
                    RespValue.BulkString("ACL LIST -- list configured users (single default user)"),
                    RespValue.BulkString("ACL USERS -- list usernames"),
                    RespValue.BulkString("NovaDB uses AUTH password; full ACL SETUSER is not implemented")
                ]));

            default:
                return ValueTask.FromResult(
                    RespValue.Error($"ERR unknown subcommand or wrong number of arguments for '{sub}'. Try ACL HELP."));
        }
    }
}
