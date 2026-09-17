using System.Globalization;
using NovaDB.Commands.Internal;
using NovaDB.Core.Models;
using NovaDB.Protocol;
using NovaDB.Storage;
using NovaDB.Storage.Values;

namespace NovaDB.Commands.Handlers;

/// <summary>Handles the ZADD command.</summary>
public sealed class ZaddCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="ZaddCommandHandler"/> class.
    /// </summary>
    public ZaddCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "ZADD";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 4);

        if ((context.Arguments.Length - 2) % 2 != 0)
        {
            throw Core.Exceptions.SyntaxException.WrongNumberOfArguments(Name);
        }

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var added = await _storage.MutateAsync(
            key,
            context,
            static (ctx, scope) =>
            {
                var entry = scope.Entry;
                var sortedSet = entry is null
                    ? new SortedSetValue()
                    : StorageEntryHelper.GetSortedSetValue(entry);

                long count = 0;
                for (var i = 2; i < ctx.Arguments.Length; i += 2)
                {
                    var score = CommandArgumentReader.GetDouble(ctx.Arguments[i], "score");
                    var member = CommandArgumentReader.GetBulkBytes(ctx.Arguments[i + 1], "member");
                    if (sortedSet.Add(score, member))
                    {
                        count++;
                    }
                }

                if (entry is null)
                {
                    scope.Replace(StorageEntryHelper.CreateEntry(RedisValueType.SortedSet, sortedSet));
                }
                else
                {
                    scope.MarkMutated();
                }

                return count;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.FromInteger(added);
    }
}

/// <summary>Handles the ZRANGE command.</summary>
public sealed class ZrangeCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="ZrangeCommandHandler"/> class.
    /// </summary>
    public ZrangeCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "ZRANGE";

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

        var sortedSet = StorageEntryHelper.GetSortedSetValue(entry);
        var members = sortedSet.RangeByRank(start, stop);
        var result = members.Select(static m => RespValue.BulkString(m)).ToArray();
        return RespValue.FromArray(result);
    }
}

/// <summary>Handles the ZSCORE command.</summary>
public sealed class ZscoreCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="ZscoreCommandHandler"/> class.
    /// </summary>
    public ZscoreCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "ZSCORE";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 3);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var member = CommandArgumentReader.GetBulkBytes(context.Arguments[2], "member");
        var entry = await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false);

        if (entry is null)
        {
            return RespValue.NullBulk();
        }

        var sortedSet = StorageEntryHelper.GetSortedSetValue(entry);
        var score = sortedSet.GetScore(member);

        return score is null
            ? RespValue.NullBulk()
            : RespValue.BulkString(score.Value.ToString(CultureInfo.InvariantCulture));
    }
}
