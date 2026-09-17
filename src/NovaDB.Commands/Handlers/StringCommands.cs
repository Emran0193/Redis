using NovaDB.Commands.Internal;
using NovaDB.Core.Models;
using NovaDB.Protocol;
using NovaDB.Storage;
using NovaDB.Storage.Values;

namespace NovaDB.Commands.Handlers;

/// <summary>Handles the SET command.</summary>
public sealed class SetCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="SetCommandHandler"/> class.
    /// </summary>
    public SetCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "SET";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 3);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var valueBytes = CommandArgumentReader.GetBulkBytes(context.Arguments[2], "value");
        var options = ParseOptions(context.Arguments);

        var result = await _storage.MutateAsync(
            key,
            new SetState(valueBytes, options),
            static (state, scope) =>
            {
                var exists = scope.Entry is not null;
                if (state.Options.Nx && exists)
                {
                    return new SetMutationResult(Applied: false, Previous: null);
                }

                if (state.Options.Xx && !exists)
                {
                    return new SetMutationResult(Applied: false, Previous: null);
                }

                byte[]? previous = null;
                if (state.Options.Get && scope.Entry?.Value is StringValue existing)
                {
                    previous = existing.Bytes;
                }

                var expireAtUnixMs = state.Options.KeepTtl
                    ? scope.Entry?.ExpireAtUnixMs
                    : state.Options.ExpireAtUnixMs;

                scope.Replace(StorageEntryHelper.CreateEntry(
                    RedisValueType.String,
                    new StringValue(state.Value),
                    expireAtUnixMs));

                return new SetMutationResult(Applied: true, Previous: previous);
            },
            context.CancellationToken).ConfigureAwait(false);

        if (!result.Applied)
        {
            return RespValue.NullBulk();
        }

        if (options.Get)
        {
            return result.Previous is null
                ? RespValue.NullBulk()
                : RespValue.BulkString(result.Previous);
        }

        return RespValue.Ok;
    }

    /// <summary>
    /// Parses the trailing SET options, which may appear in any order and any casing.
    /// </summary>
    private static SetOptions ParseOptions(RespValue[] arguments)
    {
        long? expireAtUnixMs = null;
        var keepTtl = false;
        var nx = false;
        var xx = false;
        var get = false;

        for (var i = 3; i < arguments.Length; i++)
        {
            var option = arguments[i].AsUtf8String().ToUpperInvariant();

            if (option is "NX")
            {
                if (nx || xx)
                {
                    throw new Core.Exceptions.SyntaxException("syntax error");
                }

                nx = true;
                continue;
            }

            if (option is "XX")
            {
                if (nx || xx)
                {
                    throw new Core.Exceptions.SyntaxException("syntax error");
                }

                xx = true;
                continue;
            }

            if (option is "GET")
            {
                get = true;
                continue;
            }

            if (option is "KEEPTTL")
            {
                if (keepTtl || expireAtUnixMs is not null)
                {
                    throw new Core.Exceptions.SyntaxException("syntax error");
                }

                keepTtl = true;
                continue;
            }

            if (i + 1 >= arguments.Length)
            {
                throw new Core.Exceptions.SyntaxException("syntax error");
            }

            if (option is not ("EX" or "PX" or "EXAT" or "PXAT"))
            {
                throw new Core.Exceptions.SyntaxException($"syntax error, unexpected '{option}'");
            }

            if (keepTtl || expireAtUnixMs is not null)
            {
                throw new Core.Exceptions.SyntaxException("syntax error");
            }

            expireAtUnixMs = option switch
            {
                "EX" => StorageEntryHelper.NowUnixMs()
                    + (CommandArgumentReader.GetInteger(arguments[i + 1], "seconds") * 1000),
                "PX" => StorageEntryHelper.NowUnixMs()
                    + CommandArgumentReader.GetInteger(arguments[i + 1], "milliseconds"),
                "EXAT" => CommandArgumentReader.GetInteger(arguments[i + 1], "unix-time-seconds") * 1000,
                _ => CommandArgumentReader.GetInteger(arguments[i + 1], "unix-time-milliseconds"),
            };

            i++;
        }

        return new SetOptions(expireAtUnixMs, keepTtl, nx, xx, get);
    }

    private readonly record struct SetOptions(
        long? ExpireAtUnixMs,
        bool KeepTtl,
        bool Nx,
        bool Xx,
        bool Get);

    private readonly record struct SetState(byte[] Value, SetOptions Options);

    private readonly record struct SetMutationResult(bool Applied, byte[]? Previous);
}

/// <summary>Handles the GET command.</summary>
public sealed class GetCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="GetCommandHandler"/> class.
    /// </summary>
    public GetCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "GET";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 2);
        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var entry = await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false);

        if (entry is null)
        {
            return RespValue.NullBulk();
        }

        var stringValue = StorageEntryHelper.GetStringValue(entry);
        return RespValue.BulkString(stringValue.Bytes);
    }
}

/// <summary>Handles SETEX — SET with expire in seconds.</summary>
public sealed class SetexCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="SetexCommandHandler"/> class.
    /// </summary>
    public SetexCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "SETEX";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 4);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var seconds = CommandArgumentReader.GetInteger(context.Arguments[2], "seconds");
        var valueBytes = CommandArgumentReader.GetBulkBytes(context.Arguments[3], "value");

        if (seconds <= 0)
        {
            throw new Core.Exceptions.SyntaxException("invalid expire time in 'SETEX' command");
        }

        var expireAt = StorageEntryHelper.NowUnixMs() + (seconds * 1000);
        await _storage.MutateAsync(
            key,
            new SetexState(valueBytes, expireAt),
            static (state, scope) =>
            {
                scope.Replace(StorageEntryHelper.CreateEntry(
                    RedisValueType.String,
                    new StringValue(state.Value),
                    state.ExpireAtUnixMs));
                return true;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.Ok;
    }

    private readonly record struct SetexState(byte[] Value, long ExpireAtUnixMs);
}

/// <summary>Handles SETNX — set if not exists (legacy; StackExchange.Redis still emits it).</summary>
public sealed class SetnxCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="SetnxCommandHandler"/> class.
    /// </summary>
    public SetnxCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "SETNX";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 3);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var valueBytes = CommandArgumentReader.GetBulkBytes(context.Arguments[2], "value");

        var applied = await _storage.MutateAsync(
            key,
            valueBytes,
            static (value, scope) =>
            {
                if (scope.Entry is not null)
                {
                    return false;
                }

                scope.Replace(StorageEntryHelper.CreateEntry(
                    RedisValueType.String,
                    new StringValue(value),
                    expireAtUnixMs: null));
                return true;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.FromInteger(applied ? 1 : 0);
    }
}

/// <summary>Handles GETEX — GET and optionally set a new expiration.</summary>
public sealed class GetexCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="GetexCommandHandler"/> class.
    /// </summary>
    public GetexCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "GETEX";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 2);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var (expireAtUnixMs, persist) = ParseGetexOptions(context.Arguments);

        var result = await _storage.MutateAsync(
            key,
            new GetexState(expireAtUnixMs, persist),
            static (state, scope) =>
            {
                if (scope.Entry is null || scope.Entry.Value is not StringValue stringValue)
                {
                    return (byte[]?)null;
                }

                if (state.Persist)
                {
                    scope.Entry.ExpireAtUnixMs = null;
                    scope.MarkMutated();
                }
                else if (state.ExpireAtUnixMs is long expireAt)
                {
                    scope.Entry.ExpireAtUnixMs = expireAt;
                    scope.MarkMutated();
                }

                return stringValue.Bytes;
            },
            context.CancellationToken).ConfigureAwait(false);

        return result is null ? RespValue.NullBulk() : RespValue.BulkString(result);
    }

    private static (long? ExpireAtUnixMs, bool Persist) ParseGetexOptions(RespValue[] arguments)
    {
        if (arguments.Length == 2)
        {
            return (null, false);
        }

        long? expireAtUnixMs = null;
        var persist = false;

        for (var i = 2; i < arguments.Length; i++)
        {
            var option = arguments[i].AsUtf8String().ToUpperInvariant();
            if (option is "PERSIST")
            {
                if (persist || expireAtUnixMs is not null)
                {
                    throw new Core.Exceptions.SyntaxException("syntax error");
                }

                persist = true;
                continue;
            }

            if (i + 1 >= arguments.Length)
            {
                throw new Core.Exceptions.SyntaxException("syntax error");
            }

            if (option is not ("EX" or "PX" or "EXAT" or "PXAT"))
            {
                throw new Core.Exceptions.SyntaxException($"syntax error, unexpected '{option}'");
            }

            if (persist || expireAtUnixMs is not null)
            {
                throw new Core.Exceptions.SyntaxException("syntax error");
            }

            expireAtUnixMs = option switch
            {
                "EX" => StorageEntryHelper.NowUnixMs()
                    + (CommandArgumentReader.GetInteger(arguments[i + 1], "seconds") * 1000),
                "PX" => StorageEntryHelper.NowUnixMs()
                    + CommandArgumentReader.GetInteger(arguments[i + 1], "milliseconds"),
                "EXAT" => CommandArgumentReader.GetInteger(arguments[i + 1], "unix-time-seconds") * 1000,
                _ => CommandArgumentReader.GetInteger(arguments[i + 1], "unix-time-milliseconds"),
            };
            i++;
        }

        return (expireAtUnixMs, persist);
    }

    private readonly record struct GetexState(long? ExpireAtUnixMs, bool Persist);
}

/// <summary>Handles the MGET command.</summary>
public sealed class MgetCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="MgetCommandHandler"/> class.
    /// </summary>
    public MgetCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "MGET";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 2);

        var results = new RespValue[context.Arguments.Length - 1];
        for (var i = 1; i < context.Arguments.Length; i++)
        {
            var key = CommandArgumentReader.GetKey(context.Arguments[i], "key");
            var entry = await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false);

            if (entry is null || entry.Type != RedisValueType.String)
            {
                results[i - 1] = RespValue.NullBulk();
                continue;
            }

            var stringValue = (StringValue)entry.Value;
            results[i - 1] = RespValue.BulkString(stringValue.Bytes);
        }

        return RespValue.FromArray(results);
    }
}

/// <summary>Handles the MSET command.</summary>
public sealed class MsetCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="MsetCommandHandler"/> class.
    /// </summary>
    public MsetCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "MSET";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Length < 3 || (context.Arguments.Length - 1) % 2 != 0)
        {
            throw Core.Exceptions.SyntaxException.WrongNumberOfArguments(Name);
        }

        for (var i = 1; i < context.Arguments.Length; i += 2)
        {
            var key = CommandArgumentReader.GetKey(context.Arguments[i], "key");
            var valueBytes = CommandArgumentReader.GetBulkBytes(context.Arguments[i + 1], "value");
            await _storage.MutateAsync(
                key,
                valueBytes,
                static (value, scope) =>
                {
                    scope.Replace(StorageEntryHelper.CreateEntry(
                        RedisValueType.String,
                        new StringValue(value),
                        expireAtUnixMs: null));
                    return true;
                },
                context.CancellationToken).ConfigureAwait(false);
        }

        return RespValue.Ok;
    }
}

/// <summary>Handles the DEL command.</summary>
public sealed class DelCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="DelCommandHandler"/> class.
    /// </summary>
    public DelCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "DEL";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 2);

        long deleted = 0;
        for (var i = 1; i < context.Arguments.Length; i++)
        {
            var key = CommandArgumentReader.GetKey(context.Arguments[i], "key");
            if (await _storage.DeleteAsync(key, context.CancellationToken).ConfigureAwait(false))
            {
                deleted++;
            }
        }

        return RespValue.FromInteger(deleted);
    }
}

/// <summary>Handles the EXISTS command.</summary>
public sealed class ExistsCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExistsCommandHandler"/> class.
    /// </summary>
    public ExistsCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "EXISTS";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 2);

        long count = 0;
        for (var i = 1; i < context.Arguments.Length; i++)
        {
            var key = CommandArgumentReader.GetKey(context.Arguments[i], "key");
            count += await _storage.ExistsAsync(key, context.CancellationToken).ConfigureAwait(false);
        }

        return RespValue.FromInteger(count);
    }
}

/// <summary>Handles the INCR command.</summary>
public sealed class IncrCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncrCommandHandler"/> class.
    /// </summary>
    public IncrCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "INCR";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 2);
        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var value = await _storage.IncrementAsync(key, 1, context.CancellationToken).ConfigureAwait(false);
        return RespValue.FromInteger(value);
    }
}

/// <summary>Handles the DECR command.</summary>
public sealed class DecrCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="DecrCommandHandler"/> class.
    /// </summary>
    public DecrCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "DECR";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 2);
        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var value = await _storage.IncrementAsync(key, -1, context.CancellationToken).ConfigureAwait(false);
        return RespValue.FromInteger(value);
    }
}

/// <summary>Handles the APPEND command.</summary>
public sealed class AppendCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppendCommandHandler"/> class.
    /// </summary>
    public AppendCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "APPEND";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 3);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var suffix = CommandArgumentReader.GetBulkBytes(context.Arguments[2], "value");

        var length = await _storage.MutateAsync(
            key,
            suffix,
            static (bytes, scope) =>
            {
                if (scope.Entry is null)
                {
                    scope.Replace(StorageEntryHelper.CreateEntry(
                        RedisValueType.String,
                        new StringValue(bytes)));
                    return (long)bytes.Length;
                }

                var stringValue = StorageEntryHelper.GetStringValue(scope.Entry);
                var combined = new byte[stringValue.Bytes.Length + bytes.Length];
                Buffer.BlockCopy(stringValue.Bytes, 0, combined, 0, stringValue.Bytes.Length);
                Buffer.BlockCopy(bytes, 0, combined, stringValue.Bytes.Length, bytes.Length);
                stringValue.Bytes = combined;
                scope.MarkMutated();
                return (long)combined.Length;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.FromInteger(length);
    }
}

/// <summary>Handles MSETNX — set multiple keys only when none already exist.</summary>
public sealed class MsetnxCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="MsetnxCommandHandler"/> class.
    /// </summary>
    public MsetnxCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "MSETNX";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        if (context.Arguments.Length < 3 || (context.Arguments.Length - 1) % 2 != 0)
        {
            throw Core.Exceptions.SyntaxException.WrongNumberOfArguments(Name);
        }

        var pairs = new (RedisKey Key, byte[] Value)[(context.Arguments.Length - 1) / 2];
        for (var i = 1; i < context.Arguments.Length; i += 2)
        {
            pairs[(i - 1) / 2] = (
                CommandArgumentReader.GetKey(context.Arguments[i], "key"),
                CommandArgumentReader.GetBulkBytes(context.Arguments[i + 1], "value"));
        }

        var applied = await _storage.RunExclusiveAsync(
            async ct =>
            {
                foreach (var (key, _) in pairs)
                {
                    if (await _storage.ExistsAsync(key, ct).ConfigureAwait(false) == 1)
                    {
                        return false;
                    }
                }

                foreach (var (key, value) in pairs)
                {
                    await _storage.MutateAsync(
                        key,
                        value,
                        static (bytes, scope) =>
                        {
                            scope.Replace(StorageEntryHelper.CreateEntry(
                                RedisValueType.String,
                                new StringValue(bytes),
                                expireAtUnixMs: null));
                            return true;
                        },
                        ct).ConfigureAwait(false);
                }

                return true;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.FromInteger(applied ? 1 : 0);
    }
}

/// <summary>Handles INCRBY — increment by integer delta.</summary>
public sealed class IncrbyCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncrbyCommandHandler"/> class.
    /// </summary>
    public IncrbyCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "INCRBY";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 3);
        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var delta = CommandArgumentReader.GetInteger(context.Arguments[2], "increment");
        var value = await _storage.IncrementAsync(key, delta, context.CancellationToken).ConfigureAwait(false);
        return RespValue.FromInteger(value);
    }
}

/// <summary>Handles DECRBY — decrement by integer delta.</summary>
public sealed class DecrbyCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="DecrbyCommandHandler"/> class.
    /// </summary>
    public DecrbyCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "DECRBY";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 3);
        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var delta = CommandArgumentReader.GetInteger(context.Arguments[2], "decrement");
        var value = await _storage.IncrementAsync(key, -delta, context.CancellationToken).ConfigureAwait(false);
        return RespValue.FromInteger(value);
    }
}

/// <summary>Handles INCRBYFLOAT — increment by floating-point delta.</summary>
public sealed class IncrbyfloatCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="IncrbyfloatCommandHandler"/> class.
    /// </summary>
    public IncrbyfloatCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "INCRBYFLOAT";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 3);
        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var delta = CommandArgumentReader.GetDouble(context.Arguments[2], "increment");

        var text = await _storage.MutateAsync(
            key,
            delta,
            static (amount, scope) =>
            {
                if (scope.Entry is null)
                {
                    var created = new StringValue([]);
                    created.SetDouble(amount);
                    scope.Replace(StorageEntryHelper.CreateEntry(RedisValueType.String, created));
                    return created.Bytes;
                }

                var stringValue = StorageEntryHelper.GetStringValue(scope.Entry);
                if (!stringValue.TryParseDouble(out var current))
                {
                    throw new Core.Exceptions.NovaDbCommandException("value is not a valid float");
                }

                stringValue.SetDouble(current + amount);
                scope.MarkMutated();
                return stringValue.Bytes;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.BulkString(text);
    }
}

/// <summary>Handles GETDEL — get string value then delete the key.</summary>
public sealed class GetdelCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="GetdelCommandHandler"/> class.
    /// </summary>
    public GetdelCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "GETDEL";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 2);
        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");

        var previous = await _storage.MutateAsync(
            key,
            0,
            static (_, scope) =>
            {
                if (scope.Entry is null || scope.Entry.Value is not StringValue stringValue)
                {
                    if (scope.Entry is not null)
                    {
                        throw new Core.Exceptions.WrongTypeException();
                    }

                    return (byte[]?)null;
                }

                var bytes = stringValue.Bytes;
                scope.Remove();
                return bytes;
            },
            context.CancellationToken).ConfigureAwait(false);

        return previous is null ? RespValue.NullBulk() : RespValue.BulkString(previous);
    }
}

/// <summary>Handles UNLINK — same as DEL on single-node (async unlink not required).</summary>
public sealed class UnlinkCommandHandler : ICommandHandler
{
    private readonly DelCommandHandler _del;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnlinkCommandHandler"/> class.
    /// </summary>
    public UnlinkCommandHandler(IStorageEngine storage) => _del = new DelCommandHandler(storage);

    /// <inheritdoc />
    public string Name => "UNLINK";

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteAsync(CommandContext context) => _del.ExecuteAsync(context);
}

/// <summary>Handles TOUCH — refresh LRU/access time without changing the value.</summary>
public sealed class TouchCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="TouchCommandHandler"/> class.
    /// </summary>
    public TouchCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "TOUCH";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 2);

        long touched = 0;
        for (var i = 1; i < context.Arguments.Length; i++)
        {
            var key = CommandArgumentReader.GetKey(context.Arguments[i], "key");
            if (await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false) is not null)
            {
                touched++;
            }
        }

        return RespValue.FromInteger(touched);
    }
}

/// <summary>Handles STRLEN — length of a string value.</summary>
public sealed class StrlenCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrlenCommandHandler"/> class.
    /// </summary>
    public StrlenCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "STRLEN";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 2);
        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var entry = await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return RespValue.FromInteger(0);
        }

        var stringValue = StorageEntryHelper.GetStringValue(entry);
        return RespValue.FromInteger(stringValue.Bytes.Length);
    }
}

/// <summary>Handles PSETEX — SET with expire in milliseconds.</summary>
public sealed class PsetexCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="PsetexCommandHandler"/> class.
    /// </summary>
    public PsetexCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "PSETEX";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 4);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var milliseconds = CommandArgumentReader.GetInteger(context.Arguments[2], "milliseconds");
        var valueBytes = CommandArgumentReader.GetBulkBytes(context.Arguments[3], "value");

        if (milliseconds <= 0)
        {
            throw new Core.Exceptions.SyntaxException("invalid expire time in 'PSETEX' command");
        }

        var expireAt = StorageEntryHelper.NowUnixMs() + milliseconds;
        await _storage.MutateAsync(
            key,
            new PsetexState(valueBytes, expireAt),
            static (state, scope) =>
            {
                scope.Replace(StorageEntryHelper.CreateEntry(
                    RedisValueType.String,
                    new StringValue(state.Value),
                    state.ExpireAtUnixMs));
                return true;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.Ok;
    }

    private readonly record struct PsetexState(byte[] Value, long ExpireAtUnixMs);
}

