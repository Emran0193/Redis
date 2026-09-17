using NovaDB.Commands.Internal;
using NovaDB.Core.Models;
using NovaDB.Protocol;
using NovaDB.Storage;
using NovaDB.Storage.Values;

namespace NovaDB.Commands.Handlers;

/// <summary>Handles the HSET command.</summary>
public sealed class HsetCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="HsetCommandHandler"/> class.
    /// </summary>
    public HsetCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "HSET";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 4);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var fieldBytes = CommandArgumentReader.GetBulkBytes(context.Arguments[2], "field");
        var valueBytes = CommandArgumentReader.GetBulkBytes(context.Arguments[3], "value");

        if (fieldBytes.Length == 0)
        {
            throw new Core.Exceptions.NovaDbCommandException("field must not be empty");
        }

        var added = await _storage.MutateAsync(
            key,
            new HsetState(fieldBytes, valueBytes),
            static (state, scope) =>
            {
                var entry = scope.Entry;
                var hash = entry is null ? new HashValue() : StorageEntryHelper.GetHashValue(entry);
                var created = hash.SetField(state.Field, state.Value);

                if (entry is null)
                {
                    scope.Replace(StorageEntryHelper.CreateEntry(RedisValueType.Hash, hash));
                }
                else
                {
                    scope.MarkMutated();
                }

                return created;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.FromInteger(added ? 1 : 0);
    }

    private readonly record struct HsetState(byte[] Field, byte[] Value);
}

/// <summary>Handles the HGET command.</summary>
public sealed class HgetCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="HgetCommandHandler"/> class.
    /// </summary>
    public HgetCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "HGET";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 3);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var field = CommandArgumentReader.GetBulkBytes(context.Arguments[2], "field");
        var entry = await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false);

        if (entry is null)
        {
            return RespValue.NullBulk();
        }

        var hash = StorageEntryHelper.GetHashValue(entry);
        return hash.TryGetValue(field, out var value)
            ? RespValue.BulkString(value)
            : RespValue.NullBulk();
    }
}

/// <summary>Handles the HDEL command.</summary>
public sealed class HdelCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="HdelCommandHandler"/> class.
    /// </summary>
    public HdelCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "HDEL";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 3);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var deleted = await _storage.MutateAsync(
            key,
            context,
            static (ctx, scope) =>
            {
                if (scope.Entry is null)
                {
                    return 0L;
                }

                var hash = StorageEntryHelper.GetHashValue(scope.Entry);
                var removed = 0L;
                for (var i = 2; i < ctx.Arguments.Length; i++)
                {
                    var field = CommandArgumentReader.GetBulkBytes(ctx.Arguments[i], "field");
                    if (hash.RemoveField(field))
                    {
                        removed++;
                    }
                }

                if (hash.Count == 0)
                {
                    scope.Remove();
                }
                else if (removed > 0)
                {
                    scope.MarkMutated();
                }

                return removed;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.FromInteger(deleted);
    }
}

/// <summary>Handles the HGETALL command.</summary>
public sealed class HgetallCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="HgetallCommandHandler"/> class.
    /// </summary>
    public HgetallCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "HGETALL";

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

        var hash = StorageEntryHelper.GetHashValue(entry);
        var flat = hash.GetAllFlat();
        var result = new RespValue[flat.Length];
        for (var i = 0; i < flat.Length; i++)
        {
            result[i] = RespValue.BulkString(flat[i]);
        }

        return RespValue.FromArray(result);
    }
}

/// <summary>Handles HMGET — get multiple hash fields.</summary>
public sealed class HmgetCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="HmgetCommandHandler"/> class.
    /// </summary>
    public HmgetCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "HMGET";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 3);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var entry = await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false);
        HashValue? hash = entry is null ? null : StorageEntryHelper.GetHashValue(entry);

        var results = new RespValue[context.Arguments.Length - 2];
        for (var i = 2; i < context.Arguments.Length; i++)
        {
            var field = CommandArgumentReader.GetBulkBytes(context.Arguments[i], "field");
            if (hash is not null && hash.TryGetValue(field, out var value))
            {
                results[i - 2] = RespValue.BulkString(value);
            }
            else
            {
                results[i - 2] = RespValue.NullBulk();
            }
        }

        return RespValue.FromArray(results);
    }
}

/// <summary>Handles HINCRBY — increment a hash field by an integer.</summary>
public sealed class HincrbyCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="HincrbyCommandHandler"/> class.
    /// </summary>
    public HincrbyCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "HINCRBY";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 4);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var field = CommandArgumentReader.GetBulkBytes(context.Arguments[2], "field");
        var delta = CommandArgumentReader.GetInteger(context.Arguments[3], "increment");

        var updated = await _storage.MutateAsync(
            key,
            new HincrbyState(field, delta),
            static (state, scope) =>
            {
                var entry = scope.Entry;
                var hash = entry is null ? new HashValue() : StorageEntryHelper.GetHashValue(entry);

                long current = 0;
                if (hash.TryGetValue(state.Field, out var existing))
                {
                    if (!long.TryParse(
                            System.Text.Encoding.UTF8.GetString(existing),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out current))
                    {
                        throw new Core.Exceptions.NovaDbCommandException(
                            "hash value is not an integer");
                    }
                }

                var next = current + state.Delta;
                hash.SetField(
                    state.Field,
                    System.Text.Encoding.UTF8.GetBytes(
                        next.ToString(System.Globalization.CultureInfo.InvariantCulture)));

                if (entry is null)
                {
                    scope.Replace(StorageEntryHelper.CreateEntry(RedisValueType.Hash, hash));
                }
                else
                {
                    scope.MarkMutated();
                }

                return next;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.FromInteger(updated);
    }

    private readonly record struct HincrbyState(byte[] Field, long Delta);
}

/// <summary>Handles HEXISTS — whether a hash field exists.</summary>
public sealed class HexistsCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="HexistsCommandHandler"/> class.
    /// </summary>
    public HexistsCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "HEXISTS";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 3);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var field = CommandArgumentReader.GetBulkBytes(context.Arguments[2], "field");
        var entry = await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return RespValue.FromInteger(0);
        }

        var hash = StorageEntryHelper.GetHashValue(entry);
        return RespValue.FromInteger(hash.ContainsField(field) ? 1 : 0);
    }
}

/// <summary>Handles HLEN — number of fields in a hash.</summary>
public sealed class HlenCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlenCommandHandler"/> class.
    /// </summary>
    public HlenCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "HLEN";

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

        return RespValue.FromInteger(StorageEntryHelper.GetHashValue(entry).Count);
    }
}

/// <summary>Handles HKEYS — list hash field names.</summary>
public sealed class HkeysCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="HkeysCommandHandler"/> class.
    /// </summary>
    public HkeysCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "HKEYS";

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

        var fields = StorageEntryHelper.GetHashValue(entry).GetFieldBytes();
        var result = new RespValue[fields.Count];
        var i = 0;
        foreach (var field in fields)
        {
            result[i++] = RespValue.BulkString(field);
        }

        return RespValue.FromArray(result);
    }
}

/// <summary>Handles HVALS — list hash field values.</summary>
public sealed class HvalsCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="HvalsCommandHandler"/> class.
    /// </summary>
    public HvalsCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "HVALS";

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

        var values = StorageEntryHelper.GetHashValue(entry).GetValues();
        var result = new RespValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            result[i] = RespValue.BulkString(values[i]);
        }

        return RespValue.FromArray(result);
    }
}
