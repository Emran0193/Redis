using Microsoft.Extensions.Logging;
using NovaDB.Core.Sessions;
using NovaDB.Networking;
using NovaDB.Persistence.Aof;
using NovaDB.Protocol;

namespace NovaDB.Commands.Persistence;

/// <summary>
/// Replays append-only log records through the real command handlers.
/// </summary>
/// <remarks>
/// Recovery deliberately shares one code path with live command execution. A hand-written replayer
/// only covers the commands someone remembered to add to it, and every command it misses is silent
/// data loss; here an unknown or failing record aborts startup instead.
/// </remarks>
public sealed class DispatchingAofCommandReplayer : IAofCommandReplayer
{
    private readonly ICommandExecutor _executor;
    private readonly IServiceProvider _services;
    private readonly ILogger<DispatchingAofCommandReplayer> _logger;
    private readonly ReplayConnection _connection = new();
    private long _recordNumber;

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatchingAofCommandReplayer"/> class.
    /// </summary>
    /// <param name="executor">Command executor.</param>
    /// <param name="services">Root service provider handed to command handlers.</param>
    /// <param name="logger">Logger instance.</param>
    public DispatchingAofCommandReplayer(
        ICommandExecutor executor,
        IServiceProvider services,
        ILogger<DispatchingAofCommandReplayer> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask ReplayAsync(RespValue command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        _recordNumber++;

        if (command.Type != RespType.Array || command.Array is null || command.Array.Length == 0)
        {
            throw new AofReplayException(
                $"AOF record {_recordNumber} is not a command array.");
        }

        var context = new CommandContext
        {
            Connection = _connection,
            Session = _connection.Session,
            Arguments = command.Array,
            Services = _services,
            CancellationToken = cancellationToken,
            IsReplay = true
        };

        RespValue response;
        try
        {
            response = await _executor.ExecuteDirectAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or AofReplayException))
        {
            throw new AofReplayException(
                $"AOF record {_recordNumber} ('{context.CommandName}') threw during replay.",
                ex);
        }

        if (response.Type == RespType.Error)
        {
            throw new AofReplayException(
                $"AOF record {_recordNumber} ('{context.CommandName}') was rejected during replay: {response.Simple}");
        }

        if (_recordNumber % 100_000 == 0)
        {
            _logger.LogInformation("Replayed {RecordCount} AOF records.", _recordNumber);
        }
    }

    /// <summary>
    /// Stand-in connection used while replaying; it has no socket and discards pushed messages.
    /// </summary>
    private sealed class ReplayConnection : IClientConnection
    {
        public string ConnectionId => "aof-replay";

        public string RemoteAddress => "aof-replay";

        public bool IsAuthenticated { get; set; } = true;

        public bool IsSubscribed { get; set; }

        public bool ShouldClose { get; set; }

        public ClientSession Session { get; } = new("aof-replay") { IsAuthenticated = true };

        public void RequestClose() => ShouldClose = true;

        public ValueTask PushMessageAsync(RespValue message, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
