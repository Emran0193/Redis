using System.Text;
using System.Text.RegularExpressions;
using NovaDB.Commands.Internal;
using NovaDB.Core.Exceptions;
using NovaDB.Core.Models;
using NovaDB.Protocol;
using NovaDB.Storage;

namespace NovaDB.Commands.Handlers;

/// <summary>Handles DBSIZE.</summary>
public sealed class DbsizeCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>Initializes a new instance of the <see cref="DbsizeCommandHandler"/> class.</summary>
    public DbsizeCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "DBSIZE";

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 1);
        return ValueTask.FromResult(RespValue.FromInteger(_storage.KeyCount));
    }
}

/// <summary>Handles FLUSHDB / FLUSHALL (single-DB engine, same behaviour).</summary>
public sealed class FlushdbCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;
    private readonly Security.IAuditTrail? _audit;

    /// <summary>Initializes a new instance of the <see cref="FlushdbCommandHandler"/> class.</summary>
    public FlushdbCommandHandler(IStorageEngine storage, Security.IAuditTrail? audit = null)
    {
        _storage = storage;
        _audit = audit;
    }

    /// <inheritdoc />
    public string Name => "FLUSHDB";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        await FlushAsync(_storage, context.CancellationToken).ConfigureAwait(false);
        _audit?.Record(context.Session.ConnectionId, "FLUSHDB", "all keys");
        return RespValue.Ok;
    }

    internal static async ValueTask FlushAsync(IStorageEngine storage, CancellationToken cancellationToken)
    {
        var keys = new List<RedisKey>();
        await foreach (var (key, _) in storage.ScanAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add(key);
        }

        foreach (var key in keys)
        {
            await storage.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Handles FLUSHALL.</summary>
public sealed class FlushallCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;
    private readonly Security.IAuditTrail? _audit;

    /// <summary>Initializes a new instance of the <see cref="FlushallCommandHandler"/> class.</summary>
    public FlushallCommandHandler(IStorageEngine storage, Security.IAuditTrail? audit = null)
    {
        _storage = storage;
        _audit = audit;
    }

    /// <inheritdoc />
    public string Name => "FLUSHALL";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        await FlushdbCommandHandler.FlushAsync(_storage, context.CancellationToken).ConfigureAwait(false);
        _audit?.Record(context.Session.ConnectionId, "FLUSHALL", "all keys");
        return RespValue.Ok;
    }
}

/// <summary>Handles KEYS pattern (blocking full scan — fine for admin use).</summary>
public sealed class KeysCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>Initializes a new instance of the <see cref="KeysCommandHandler"/> class.</summary>
    public KeysCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "KEYS";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 2);
        var pattern = context.Arguments[1].AsUtf8String();
        var regex = GlobToRegex(pattern);
        var matches = new List<RespValue>();

        await foreach (var (key, _) in _storage.ScanAsync(context.CancellationToken).ConfigureAwait(false))
        {
            var text = key.ToString();
            if (regex.IsMatch(text))
            {
                matches.Add(RespValue.BulkString(key.Bytes));
            }
        }

        return RespValue.FromArray(matches.ToArray());
    }

    private static Regex GlobToRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        foreach (var ch in pattern)
        {
            sb.Append(ch switch
            {
                '*' => ".*",
                '?' => ".",
                '.' or '(' or ')' or '[' or ']' or '{' or '}' or '\\' or '+' or '^' or '$' or '|' => "\\" + ch,
                _ => ch.ToString()
            });
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}

/// <summary>Handles SCAN cursor [MATCH pattern] [COUNT count].</summary>
public sealed class ScanCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;

    /// <summary>Initializes a new instance of the <see cref="ScanCommandHandler"/> class.</summary>
    public ScanCommandHandler(IStorageEngine storage) => _storage = storage;

    /// <inheritdoc />
    public string Name => "SCAN";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 2);
        var cursor = CommandArgumentReader.GetInteger(context.Arguments[1], "cursor");
        if (cursor < 0)
        {
            throw new SyntaxException("invalid cursor");
        }

        string? pattern = null;
        var count = 10L;
        for (var i = 2; i < context.Arguments.Length; i++)
        {
            var option = context.Arguments[i].AsUtf8String().ToUpperInvariant();
            if (option == "MATCH" && i + 1 < context.Arguments.Length)
            {
                pattern = context.Arguments[++i].AsUtf8String();
            }
            else if (option == "COUNT" && i + 1 < context.Arguments.Length)
            {
                count = CommandArgumentReader.GetInteger(context.Arguments[++i], "count");
                if (count <= 0)
                {
                    count = 10;
                }
            }
            else
            {
                throw new SyntaxException("syntax error");
            }
        }

        Regex? regex = pattern is null ? null : KeysCommandHandlerGlob(pattern);
        var examined = 0;
        var batch = new List<RespValue>();
        var nextCursor = cursor;

        // Advance page-by-page until we fill COUNT matches or the cursor wraps to 0.
        // COUNT is treated as a match budget (practical for clients); storage examines one shard page at a time.
        while (batch.Count < count)
        {
            var pageSize = (int)Math.Min(Math.Max(count, 10), 1_000);
            var (pageCursor, keys) = await _storage
                .ScanKeysAsync(nextCursor, pageSize, context.CancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < keys.Count && batch.Count < count; i++)
            {
                examined++;
                var key = keys[i];
                if (regex is null || regex.IsMatch(key.ToString()))
                {
                    batch.Add(RespValue.BulkString(key.Bytes));
                }
            }

            nextCursor = pageCursor;
            if (nextCursor == 0 || keys.Count == 0)
            {
                break;
            }

            // Avoid unbounded loops when MATCH filters everything: stop after enough examinations.
            if (examined >= count * 16 && batch.Count == 0)
            {
                break;
            }

            if (batch.Count >= count)
            {
                break;
            }
        }

        return RespValue.FromArray(
        [
            RespValue.BulkString(nextCursor.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            RespValue.FromArray(batch.ToArray())
        ]);
    }

    private static Regex KeysCommandHandlerGlob(string pattern)
    {
        // Reuse KEYS glob semantics via a local copy to avoid making KeysCommandHandler's helper public.
        var sb = new StringBuilder("^");
        foreach (var ch in pattern)
        {
            sb.Append(ch switch
            {
                '*' => ".*",
                '?' => ".",
                '.' or '(' or ')' or '[' or ']' or '{' or '}' or '\\' or '+' or '^' or '$' or '|' => "\\" + ch,
                _ => ch.ToString()
            });
        }

        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
