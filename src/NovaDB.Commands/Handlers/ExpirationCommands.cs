using NovaDB.Commands.Internal;
using NovaDB.Protocol;
using NovaDB.Storage;

namespace NovaDB.Commands.Handlers;

/// <summary>Handles the EXPIRE command.</summary>
public sealed class ExpireCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExpireCommandHandler"/> class.
    /// </summary>
    public ExpireCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "EXPIRE";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 3);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var seconds = CommandArgumentReader.GetInteger(context.Arguments[2], "seconds");

        if (seconds <= 0)
        {
            return RespValue.FromInteger(0);
        }

        var applied = await _storage.MutateAsync(
            key,
            StorageEntryHelper.NowUnixMs() + (seconds * 1000),
            static (expireAtUnixMs, scope) =>
            {
                if (scope.Entry is null)
                {
                    return false;
                }

                scope.Entry.ExpireAtUnixMs = expireAtUnixMs;
                scope.MarkMutated();
                return true;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.FromInteger(applied ? 1 : 0);
    }
}

/// <summary>Handles the PEXPIREAT command.</summary>
public sealed class PexpireatCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="PexpireatCommandHandler"/> class.
    /// </summary>
    public PexpireatCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "PEXPIREAT";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 3);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var expireAtUnixMs = CommandArgumentReader.GetInteger(
            context.Arguments[2],
            "unix-time-milliseconds");

        var applied = await _storage.MutateAsync(
            key,
            new PexpireatState(expireAtUnixMs, StorageEntryHelper.NowUnixMs()),
            static (state, scope) =>
            {
                if (scope.Entry is null)
                {
                    return false;
                }

                if (state.ExpireAtUnixMs <= state.NowUnixMs)
                {
                    scope.Remove();
                    return true;
                }

                scope.Entry.ExpireAtUnixMs = state.ExpireAtUnixMs;
                scope.MarkMutated();
                return true;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.FromInteger(applied ? 1 : 0);
    }

    /// <summary>State for the PEXPIREAT mutation.</summary>
    /// <param name="ExpireAtUnixMs">Requested absolute expiry.</param>
    /// <param name="NowUnixMs">Clock reading taken outside the shard lock.</param>
    private readonly record struct PexpireatState(long ExpireAtUnixMs, long NowUnixMs);
}

/// <summary>Handles the TTL command.</summary>
public sealed class TtlCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="TtlCommandHandler"/> class.
    /// </summary>
    public TtlCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "TTL";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 2);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var entry = await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false);

        if (entry is null)
        {
            return RespValue.FromInteger(-2);
        }

        if (entry.ExpireAtUnixMs is not long expireAt)
        {
            return RespValue.FromInteger(-1);
        }

        var remainingMs = expireAt - StorageEntryHelper.NowUnixMs();
        if (remainingMs <= 0)
        {
            return RespValue.FromInteger(-2);
        }

        var remainingSeconds = (long)Math.Ceiling(remainingMs / 1000.0);
        return RespValue.FromInteger(remainingSeconds);
    }
}

/// <summary>Handles the PTTL command.</summary>
public sealed class PttlCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="PttlCommandHandler"/> class.
    /// </summary>
    public PttlCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "PTTL";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 2);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var entry = await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false);

        if (entry is null)
        {
            return RespValue.FromInteger(-2);
        }

        if (entry.ExpireAtUnixMs is not long expireAt)
        {
            return RespValue.FromInteger(-1);
        }

        var remainingMs = expireAt - StorageEntryHelper.NowUnixMs();
        return RespValue.FromInteger(remainingMs <= 0 ? -2 : remainingMs);
    }
}

/// <summary>Handles the PERSIST command.</summary>
public sealed class PersistCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="PersistCommandHandler"/> class.
    /// </summary>
    public PersistCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "PERSIST";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 2);

        var key = CommandArgumentReader.GetKey(context.Arguments[1], "key");
        var cleared = await _storage.MutateAsync(
            key,
            context,
            static (_, scope) =>
            {
                if (scope.Entry is null || scope.Entry.ExpireAtUnixMs is null)
                {
                    return false;
                }

                scope.Entry.ExpireAtUnixMs = null;
                scope.MarkMutated();
                return true;
            },
            context.CancellationToken).ConfigureAwait(false);

        return RespValue.FromInteger(cleared ? 1 : 0);
    }
}
