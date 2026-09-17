using Microsoft.Extensions.Options;
using NovaDB.Commands.Internal;
using NovaDB.Configuration;
using NovaDB.Monitoring;
using NovaDB.Protocol;
using NovaDB.Storage;

namespace NovaDB.Commands.Handlers;

/// <summary>Handles MEMORY USAGE / STATS.</summary>
public sealed class MemoryCommandHandler : ICommandHandler
{
    private readonly IStorageEngine _storage;
    private readonly IOptions<NovaDbOptions> _options;
    private readonly INovaDbMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryCommandHandler"/> class.
    /// </summary>
    public MemoryCommandHandler(
        IStorageEngine storage,
        IOptions<NovaDbOptions> options,
        INovaDbMetrics metrics)
    {
        _storage = storage;
        _options = options;
        _metrics = metrics;
    }

    /// <inheritdoc />
    public string Name => "MEMORY";

    /// <inheritdoc />
    public async ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireMinArgs(Name, context.Arguments, 2);
        var sub = context.Arguments[1].AsUtf8String().ToUpperInvariant();

        switch (sub)
        {
            case "STATS":
                var snapshot = _metrics.GetSnapshot();
                var stats =
                    $"used_memory:{_storage.EstimatedMemoryBytes}\r\n" +
                    $"maxmemory:{_options.Value.MemoryLimitBytes}\r\n" +
                    $"maxmemory_policy:{_options.Value.EvictionPolicy}\r\n" +
                    $"keys:{_storage.KeyCount}\r\n" +
                    $"evicted_keys:{snapshot.Evictions}\r\n";
                return RespValue.BulkString(stats);

            case "USAGE":
                CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 3);
                var key = CommandArgumentReader.GetKey(context.Arguments[2], "key");
                var entry = await _storage.GetAsync(key, context.CancellationToken).ConfigureAwait(false);
                if (entry is null)
                {
                    return RespValue.NullBulk();
                }

                // Approximate: key bytes + reported entry estimate via engine gauge delta is hard;
                // use storage KeyCount path — expose via a cheap estimate from entry type sizes.
                var approx = key.Bytes.Length + 64;
                if (entry.Value is Storage.Values.StringValue s)
                {
                    approx += s.Bytes.Length;
                }

                return RespValue.FromInteger(approx);

            default:
                return RespValue.Error($"ERR unknown subcommand '{sub}'. Try MEMORY USAGE|STATS.");
        }
    }
}
