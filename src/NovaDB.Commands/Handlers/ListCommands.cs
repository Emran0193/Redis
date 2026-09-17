using NovaDB.Commands.Internal;
using NovaDB.Core.Models;
using NovaDB.Protocol;
using NovaDB.Storage;
using NovaDB.Storage.Values;

namespace NovaDB.Commands.Handlers;

/// <summary>Handles the LPUSH command.</summary>
public sealed class LpushCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="LpushCommandHandler"/> class.
    /// </summary>
    public LpushCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "LPUSH";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 3);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var length = await _storage.MutateAsync(
            key,
            context,
            static (ctx, scope) =>
            {
                var entry = scope.Entry;
                var list = entry is null ? new ListValue() : StorageEntryHelper.GetListValue(entry);

                for (var i = 2; i < ctx.Arguments.Length; i++)
                {
                    list.PushHead(CommandArgumentReader.GetBulkBytes(ctx.Arguments[i], "value"));
                }

                if (entry is null)
                {
                    scope.Replace(StorageEntryHelper.CreateEntry(RedisValueType.List, list));
                }
                else
                {
                    scope.MarkMutated();
                }

                return (long)list.Count;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.FromInteger(length);
    }
}

/// <summary>Handles the RPUSH command.</summary>
public sealed class RpushCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="RpushCommandHandler"/> class.
    /// </summary>
    public RpushCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "RPUSH";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 3);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var length = await _storage.MutateAsync(
            key,
            context,
            static (ctx, scope) =>
            {
                var entry = scope.Entry;
                var list = entry is null ? new ListValue() : StorageEntryHelper.GetListValue(entry);

                for (var i = 2; i < ctx.Arguments.Length; i++)
                {
                    list.PushTail(CommandArgumentReader.GetBulkBytes(ctx.Arguments[i], "value"));
                }

                if (entry is null)
                {
                    scope.Replace(StorageEntryHelper.CreateEntry(RedisValueType.List, list));
                }
                else
                {
                    scope.MarkMutated();
                }

                return (long)list.Count;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.FromInteger(length);
    }
}

/// <summary>Handles the LPOP command.</summary>
public sealed class LpopCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="LpopCommandHandler"/> class.
    /// </summary>
    public LpopCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "LPOP";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 2);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        return await _storage.MutateAsync(
            key,
            context,
            static (_, scope) =>
            {
                if (scope.Entry is null)
                {
                    return RespValue.NullBulk();
                }

                var list = StorageEntryHelper.GetListValue(scope.Entry);
                if (!list.TryPopHead(out var value))
                {
                    return RespValue.NullBulk();
                }

                if (list.Count == 0)
                {
                    scope.Remove();
                }
                else
                {
                    scope.MarkMutated();
                }

                return RespValue.BulkString(value);
            },
            context.CancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Handles the RPOP command.</summary>
public sealed class RpopCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="RpopCommandHandler"/> class.
    /// </summary>
    public RpopCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "RPOP";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 2);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        return await _storage.MutateAsync(
            key,
            context,
            static (_, scope) =>
            {
                if (scope.Entry is null)
                {
                    return RespValue.NullBulk();
                }

                var list = StorageEntryHelper.GetListValue(scope.Entry);
                if (!list.TryPopTail(out var value))
                {
                    return RespValue.NullBulk();
                }

                if (list.Count == 0)
                {
                    scope.Remove();
                }
                else
                {
                    scope.MarkMutated();
                }

                return RespValue.BulkString(value);
            },
            context.CancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Handles the LRANGE command.</summary>
public sealed class LrangeCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="LrangeCommandHandler"/> class.
    /// </summary>
    public LrangeCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "LRANGE";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 4);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var start = CommandArgumentReader.GetInteger(context.Arguments[2], "start");
        var stop = CommandArgumentReader.GetInteger(context.Arguments[3], "stop");
        var entry = await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false);

        if (entry is null)
        {
            return RespValue.FromArray([]);
        }

        var list = StorageEntryHelper.GetListValue(entry);
        var (rangeStart, rangeStop) = ListRangeHelper.NormalizeRange(start, stop, list.Count);

        if (rangeStart < 0)
        {
            return RespValue.FromArray([]);
        }

        var count = rangeStop - rangeStart + 1;
        var result = new RespValue[count];
        for (long i = 0; i < count; i++)
        {
            result[i] = RespValue.BulkString(list.GetAt(rangeStart + i));
        }

        return RespValue.FromArray(result);
    }
}
