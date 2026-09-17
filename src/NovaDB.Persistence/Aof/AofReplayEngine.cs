using System.Buffers;
using System.IO.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Protocol;

namespace NovaDB.Persistence.Aof;

/// <summary>
/// Replays append-only log entries on startup.
/// </summary>
public sealed class AofReplayEngine
{
    private readonly NovaDbOptions _options;
    private readonly IAofCommandReplayer _commandReplayer;
    private readonly ILogger<AofReplayEngine> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="AofReplayEngine"/> class.
    /// </summary>
    public AofReplayEngine(
        IOptions<NovaDbOptions> options,
        IAofCommandReplayer commandReplayer,
        ILogger<AofReplayEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _commandReplayer = commandReplayer ?? throw new ArgumentNullException(nameof(commandReplayer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Replays the configured AOF file when present.
    /// </summary>
    public async Task<long> ReplayAsync(string aofPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aofPath);

        if (!_options.AofEnabled || !File.Exists(aofPath))
        {
            _logger.LogInformation("AOF replay skipped. Enabled={Enabled}, Path={Path}", _options.AofEnabled, aofPath);
            return 0;
        }

        await using var stream = new FileStream(
            aofPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length == 0)
        {
            _logger.LogInformation("AOF file {AofPath} is empty.", aofPath);
            return 0;
        }

        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(
            bufferSize: 64 * 1024,
            leaveOpen: true));

        var replayed = 0L;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = read.Buffer;

                while (true)
                {
                    RespValue? command;
                    SequencePosition consumed;
                    try
                    {
                        if (!RespParser.TryParse(ref buffer, out command, out consumed))
                        {
                            break;
                        }
                    }
                    catch (ProtocolException ex)
                    {
                        throw new AofReplayException(
                            $"AOF file '{aofPath}' is corrupt at record {replayed + 1}: {ex.Message}",
                            ex);
                    }

                    if (command is null)
                    {
                        break;
                    }

                    await _commandReplayer.ReplayAsync(command, cancellationToken).ConfigureAwait(false);
                    replayed++;
                    buffer = buffer.Slice(consumed);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (read.IsCompleted)
                {
                    if (!buffer.IsEmpty)
                    {
                        // A truncated final record is the expected result of a crash mid-append.
                        _logger.LogWarning(
                            "Discarding {TruncatedBytes} trailing bytes of a torn final AOF record after {ReplayedCommands} commands.",
                            buffer.Length,
                            replayed);
                    }

                    break;
                }
            }
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
        }

        _logger.LogInformation("Replayed {ReplayedCommands} AOF commands from {AofPath}", replayed, aofPath);
        return replayed;
    }
}
