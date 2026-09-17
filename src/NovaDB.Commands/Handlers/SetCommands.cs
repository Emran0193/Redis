using NovaDB.Commands.Internal;
using NovaDB.Core.Models;
using NovaDB.Protocol;
using NovaDB.Storage;
using NovaDB.Storage.Values;

namespace NovaDB.Commands.Handlers;

/// <summary>Handles the SADD command.</summary>
public sealed class SaddCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaddCommandHandler"/> class.
    /// </summary>
    public SaddCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "SADD";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 3);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var added = await _storage.MutateAsync(
            key,
            context,
            static (ctx, scope) =>
            {
                var entry = scope.Entry;
                var set = entry is null ? new SetValue() : StorageEntryHelper.GetSetValue(entry);

                long count = 0;
                for (var i = 2; i < ctx.Arguments.Length; i++)
                {
                    if (set.Add(CommandArgumentReader.GetBulkBytes(ctx.Arguments[i], "member")))
                    {
                        count++;
                    }
                }

                if (entry is null)
                {
                    scope.Replace(StorageEntryHelper.CreateEntry(RedisValueType.Set, set));
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

/// <summary>Handles the SREM command.</summary>
public sealed class SremCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="SremCommandHandler"/> class.
    /// </summary>
    public SremCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "SREM";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 3);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var removed = await _storage.MutateAsync(
            key,
            context,
            static (ctx, scope) =>
            {
                if (scope.Entry is null)
                {
                    return 0L;
                }

                var set = StorageEntryHelper.GetSetValue(scope.Entry);
                long count = 0;

                for (var i = 2; i < ctx.Arguments.Length; i++)
                {
                    if (set.Remove(CommandArgumentReader.GetBulkBytes(ctx.Arguments[i], "member")))
                    {
                        count++;
                    }
                }

                if (count > 0)
                {
                    if (set.Count == 0)
                    {
                        scope.Remove();
                    }
                    else
                    {
                        scope.MarkMutated();
                    }
                }

                return count;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.FromInteger(removed);
    }
}

/// <summary>Handles the SMEMBERS command.</summary>
public sealed class SmembersCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="SmembersCommandHandler"/> class.
    /// </summary>
    public SmembersCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "SMEMBERS";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 2);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var entry = await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false);

        if (entry is null)
        {
            return RespValue.FromArray([]);
        }

        var set = StorageEntryHelper.GetSetValue(entry);
        var members = set.GetMembers()
            .Select(static m => RespValue.BulkString(m))
            .ToArray();

        return RespValue.FromArray(members);
    }
}
