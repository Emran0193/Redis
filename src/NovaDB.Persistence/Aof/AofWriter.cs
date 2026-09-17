using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;
using NovaDB.Core.Exceptions;
using NovaDB.Core.Models;
using NovaDB.Protocol;

namespace NovaDB.Persistence.Aof;

/// <summary>
/// Background AOF writer that buffers RESP commands and flushes according to policy.
/// </summary>
/// <remarks>
/// Under <see cref="AofFlushPolicy.Always"/> the caller is not released until the record has been
/// fsynced, so a successful reply implies the command is durable. Records are group-committed: a
/// single fsync covers every record drained in one batch.
/// </remarks>
public sealed class AofWriter : IAofLog, ICommandMutationSink, IHostedService, IAsyncDisposable
{
    private const string AofFileName = "appendonly.aof";
    private const int MaxBatchSize = 1024;

    private readonly NovaDbOptions _options;
    private readonly AofFlushPolicy _flushPolicy;
    private readonly ILogger<AofWriter> _logger;
    private readonly IAofMetrics _metrics;
    private readonly Channel<AofRecord> _channel;
    private readonly object _streamLock = new();
    private readonly List<AofRecord> _batch = new(MaxBatchSize);

    private FileStream? _stream;
    private Task? _workerTask;
    private CancellationTokenSource? _workerCts;
    private PeriodicTimer? _flushTimer;
    private Exception? _writerFault;
    private int _rewriteInProgress;

    /// <summary>
    /// Initializes a new instance of the <see cref="AofWriter"/> class.
    /// </summary>
    /// <param name="options">Database options.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="metrics">Optional AOF telemetry sink.</param>
    public AofWriter(
        IOptions<NovaDbOptions> options,
        ILogger<AofWriter> logger,
        IAofMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? NullAofMetrics.Instance;
        _flushPolicy = AofFlushPolicyParser.Parse(_options.AofFlushPolicy);

        var capacity = Math.Max(1024, _options.AofWriteQueueCapacity);
        _channel = Channel.CreateBounded<AofRecord>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait
        });

        FilePath = Path.Combine(_options.DataDirectory, AofFileName);
    }

    /// <inheritdoc />
    public string FilePath { get; }

    /// <inheritdoc />
    public ValueTask OnMutatingCommandAsync(ReadOnlyMemory<byte> respBytes, CancellationToken cancellationToken)
        => AppendAsync(respBytes, cancellationToken);

    /// <inheritdoc />
    public async ValueTask OnMutatingCommandBatchAsync(
        IReadOnlyList<ReadOnlyMemory<byte>> respBytesBatch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(respBytesBatch);
        if (respBytesBatch.Count == 0)
        {
            return;
        }

        if (!_options.AofEnabled)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _writerFault) is Exception fault)
        {
            throw new NovaDbCommandException(
                $"AOF is unavailable, writes are refused: {fault.Message}");
        }

        if (_stream is null)
        {
            throw new NovaDbCommandException("AOF writer is not running, writes are refused");
        }

        if (_flushPolicy != AofFlushPolicy.Always)
        {
            for (var i = 0; i < respBytesBatch.Count; i++)
            {
                await _channel.Writer
                    .WriteAsync(new AofRecord(respBytesBatch[i], null), cancellationToken)
                    .ConfigureAwait(false);
            }

            PublishQueueDepth();
            return;
        }

        // Group-commit: await fsync only on the last record so one Always drain covers the EXEC.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var i = 0; i < respBytesBatch.Count; i++)
        {
            var isLast = i == respBytesBatch.Count - 1;
            await _channel.Writer
                .WriteAsync(new AofRecord(respBytesBatch[i], isLast ? completion : null), cancellationToken)
                .ConfigureAwait(false);
        }

        PublishQueueDepth();
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask AppendAsync(ReadOnlyMemory<byte> respBytes, CancellationToken cancellationToken)
    {
        if (!_options.AofEnabled)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (Volatile.Read(ref _writerFault) is Exception fault)
        {
            throw new NovaDbCommandException(
                $"AOF is unavailable, writes are refused: {fault.Message}");
        }

        if (_stream is null)
        {
            // Refusing is the only honest answer: queueing here would either lose the record or,
            // under the always policy, block the caller forever waiting for a drain loop that is
            // not running.
            throw new NovaDbCommandException("AOF writer is not running, writes are refused");
        }

        if (_flushPolicy != AofFlushPolicy.Always)
        {
            await _channel.Writer.WriteAsync(new AofRecord(respBytes, null), cancellationToken).ConfigureAwait(false);
            PublishQueueDepth();
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync(new AofRecord(respBytes, completion), cancellationToken).ConfigureAwait(false);
        PublishQueueDepth();
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Appends a parsed RESP command by serializing it internally.
    /// </summary>
    /// <param name="command">Mutating command array.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public ValueTask AppendCommandAsync(RespValue command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var bytes = RespWriter.Serialize(command);
        return AppendAsync(bytes, cancellationToken);
    }

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken)
    {
        if (!_options.AofEnabled || _stream is null)
        {
            return Task.CompletedTask;
        }

        lock (_streamLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _stream.Flush(flushToDisk: true);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RewriteFromStorageAsync(Storage.IStorageEngine storage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (!_options.AofEnabled || _stream is null)
        {
            return;
        }

        // Serialize with BGREWRITEAOF: wait rather than fail (periodic snapshot path).
        while (Interlocked.CompareExchange(ref _rewriteInProgress, 1, 0) != 0)
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        _metrics.SetRewriteInProgress(true);
        try
        {
            await RewriteFromStorageCoreAsync(storage, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _rewriteInProgress, 0);
            _metrics.SetRewriteInProgress(false);
        }
    }

    /// <inheritdoc />
    public bool TryScheduleBackgroundRewrite(Storage.IStorageEngine storage, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (!_options.AofEnabled || _stream is null)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _rewriteInProgress, 1, 0) != 0)
        {
            return false;
        }

        _metrics.SetRewriteInProgress(true);
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await RewriteFromStorageCoreAsync(storage, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Shutdown or caller cancelled.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background AOF rewrite failed.");
                }
                finally
                {
                    Interlocked.Exchange(ref _rewriteInProgress, 0);
                    _metrics.SetRewriteInProgress(false);
                }
            },
            CancellationToken.None);

        return true;
    }

    private async Task RewriteFromStorageCoreAsync(Storage.IStorageEngine storage, CancellationToken cancellationToken)
    {
        // Drain every command that was accepted before the exclusive rewrite begins.
        await DrainPendingAsync(cancellationToken).ConfigureAwait(false);

        // Short exclusive: deep-clone dataset, then encode/write without blocking clients.
        var frozen = await storage.RunExclusiveAsync(
            async ct =>
            {
                await DrainPendingAsync(ct).ConfigureAwait(false);
                var entries = new List<(RedisKey Key, DatabaseEntry Entry)>();
                await foreach (var item in storage.ScanAsync(ct).ConfigureAwait(false))
                {
                    entries.Add(Snapshot.SnapshotEntryCloner.Clone(item.Key, item.Entry));
                }

                return entries;
            },
            cancellationToken).ConfigureAwait(false);

        var tempPath = FilePath + ".rewrite";
        await using (var temp = new FileStream(
                         tempPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 64 * 1024,
                         options: FileOptions.SequentialScan))
        {
            foreach (var (key, entry) in frozen)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var command in AofDatasetEncoder.Encode(key, entry))
                {
                    var (rented, length) = RespWriter.SerializeRented(command);
                    try
                    {
                        await temp.WriteAsync(rented.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                    }
                }
            }

            await temp.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        lock (_streamLock)
        {
            _stream!.Flush(flushToDisk: true);
            _stream.Dispose();

            if (File.Exists(FilePath))
            {
                File.Replace(tempPath, FilePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, FilePath);
            }

            _stream = new FileStream(
                FilePath,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                options: FileOptions.SequentialScan);
            _stream.Seek(0, SeekOrigin.End);
        }

        _logger.LogInformation("AOF rewritten from live dataset at {AofPath}", FilePath);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.AofEnabled)
        {
            _logger.LogInformation("AOF persistence is disabled.");
            return Task.CompletedTask;
        }

        Directory.CreateDirectory(_options.DataDirectory);
        _stream = new FileStream(
            FilePath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            // ReadWrite so recovery and external tooling can read the log while it is held open.
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        _stream.Seek(0, SeekOrigin.End);

        _workerCts = new CancellationTokenSource();
        _workerTask = RunWorkerAsync(_workerCts.Token);

        if (_flushPolicy == AofFlushPolicy.EverySec)
        {
            _flushTimer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            _ = RunPeriodicFlushAsync(_workerCts.Token);
        }

        _logger.LogInformation("AOF writer started at {AofPath} with flush policy {FlushPolicy}", FilePath, _flushPolicy);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!_options.AofEnabled)
        {
            return;
        }

        // Complete the channel first so the worker drains everything already queued.
        _channel.Writer.TryComplete();
        if (_workerTask is not null)
        {
            try
            {
                await _workerTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("AOF writer did not drain before shutdown timeout.");
            }

            _workerTask = null;
        }

        if (_flushTimer is not null)
        {
            _flushTimer.Dispose();
            _flushTimer = null;
        }

        if (_stream is not null)
        {
            lock (_streamLock)
            {
                _stream.Flush(flushToDisk: true);
                _stream.Dispose();
                _stream = null;
            }
        }

        if (_workerCts is not null)
        {
            await _workerCts.CancelAsync().ConfigureAwait(false);
            _workerCts.Dispose();
            _workerCts = null;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task DrainPendingAsync(CancellationToken cancellationToken)
    {
        // The worker owns channel reads; wait until the queue is empty and the file is flushed.
        while (_channel.Reader.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                DrainBatch();
                WriteBatch();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AOF background writer failed; refusing further writes.");
            Volatile.Write(ref _writerFault, ex);
            FailBatch(ex);
            FailRemaining(ex);
        }
    }

    private void DrainBatch()
    {
        _batch.Clear();
        while (_batch.Count < MaxBatchSize && _channel.Reader.TryRead(out var record))
        {
            _batch.Add(record);
        }

        PublishQueueDepth();
    }

    private void WriteBatch()
    {
        if (_batch.Count == 0 || _stream is null)
        {
            return;
        }

        lock (_streamLock)
        {
            for (var i = 0; i < _batch.Count; i++)
            {
                _stream.Write(_batch[i].Payload.Span);
            }

            if (_flushPolicy == AofFlushPolicy.Always)
            {
                _stream.Flush(flushToDisk: true);
            }
        }

        for (var i = 0; i < _batch.Count; i++)
        {
            _batch[i].Completion?.TrySetResult();
        }

        _batch.Clear();
    }

    private void PublishQueueDepth() => _metrics.SetQueueDepth(_channel.Reader.Count);

    private void FailBatch(Exception exception)
    {
        for (var i = 0; i < _batch.Count; i++)
        {
            _batch[i].Completion?.TrySetException(exception);
        }

        _batch.Clear();
    }

    private void FailRemaining(Exception exception)
    {
        while (_channel.Reader.TryRead(out var record))
        {
            record.Completion?.TrySetException(exception);
        }
    }

    private async Task RunPeriodicFlushAsync(CancellationToken cancellationToken)
    {
        if (_flushTimer is null)
        {
            return;
        }

        try
        {
            while (await _flushTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Expected during shutdown.
        }
    }

    private readonly struct AofRecord
    {
        public AofRecord(ReadOnlyMemory<byte> payload, TaskCompletionSource? completion)
        {
            Payload = payload;
            Completion = completion;
        }

        public ReadOnlyMemory<byte> Payload { get; }

        public TaskCompletionSource? Completion { get; }
    }
}
