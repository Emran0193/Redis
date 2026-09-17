using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NovaDB.Commands.Internal;
using NovaDB.Configuration;
using NovaDB.Persistence.Aof;
using NovaDB.Protocol;
using NovaDB.Storage;

namespace NovaDB.Commands.Handlers;

/// <summary>Handles BGREWRITEAOF — schedule a background AOF rewrite from the live dataset.</summary>
public sealed class BgRewriteAofCommandHandler : ICommandHandler
{
    private readonly IServiceProvider _services;
    private readonly IStorageEngine _storage;
    private readonly IOptions<NovaDbOptions> _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="BgRewriteAofCommandHandler"/> class.
    /// </summary>
    public BgRewriteAofCommandHandler(
        IServiceProvider services,
        IStorageEngine storage,
        IOptions<NovaDbOptions> options)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string Name => "BGREWRITEAOF";

    /// <inheritdoc />
    public ValueTask<RespValue> ExecuteAsync(CommandContext context)
    {
        CommandArgumentReader.RequireExactArgs(Name, context.Arguments, 1);

        var aofLog = _services.GetService<IAofLog>();
        if (!_options.Value.AofEnabled || aofLog is null)
        {
            return ValueTask.FromResult(RespValue.Error("ERR Append only file is disabled"));
        }

        if (!aofLog.TryScheduleBackgroundRewrite(_storage, context.CancellationToken))
        {
            return ValueTask.FromResult(
                RespValue.Error("ERR Background append only file rewriting already in progress"));
        }

        return ValueTask.FromResult(
            RespValue.SimpleString("Background append only file rewriting started"));
    }
}
