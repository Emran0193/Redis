using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NovaDB.Configuration;

namespace NovaDB.Chaos;

/// <summary>
/// Default chaos engine. Active only when <see cref="NovaDbOptions.ChaosEnabled"/> is true
/// and the host environment is Development.
/// </summary>
public sealed class ChaosFaultEngine : IChaosFaultEngine, IDisposable
{
    private readonly IOptions<NovaDbOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<ChaosFaultEngine> _logger;
    private readonly object _gate = new();
    private readonly ConcurrentBag<byte[]> _pressure = new();
    private ChaosFaultKind _active = ChaosFaultKind.None;
    private string _detail = string.Empty;
    private int _packetCounter;
    private CancellationTokenSource? _starvationCts;
    private Task? _starvationTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChaosFaultEngine"/> class.
    /// </summary>
    public ChaosFaultEngine(
        IOptions<NovaDbOptions> options,
        IHostEnvironment environment,
        ILogger<ChaosFaultEngine> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Returns whether chaos injection is permitted on this host.</summary>
    public bool IsPermitted
        => _options.Value.ChaosEnabled && _environment.IsDevelopment();

    /// <inheritdoc />
    public bool IsActive => ActiveFault != ChaosFaultKind.None;

    /// <inheritdoc />
    public ChaosFaultKind ActiveFault
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <inheritdoc />
    public string ActiveDetail
    {
        get
        {
            lock (_gate)
            {
                return _detail;
            }
        }
    }

    /// <inheritdoc />
    public void Enable(ChaosFaultKind kind, string? detail = null)
    {
        if (!IsPermitted)
        {
            throw new InvalidOperationException(
                "Chaos faults are only allowed when NovaDB:ChaosEnabled=true and the host is Development.");
        }

        if (kind == ChaosFaultKind.None)
        {
            Clear();
            return;
        }

        lock (_gate)
        {
            StopStarvation_NoLock();
            ReleasePressure_NoLock();
            _active = kind;
            _detail = detail ?? string.Empty;
            if (kind == ChaosFaultKind.MemoryPressure)
            {
                for (var i = 0; i < 32; i++)
                {
                    _pressure.Add(GC.AllocateArray<byte>(1024 * 1024, pinned: false));
                }
            }
            else if (kind == ChaosFaultKind.ThreadPoolStarvation)
            {
                _starvationCts = new CancellationTokenSource();
                var token = _starvationCts.Token;
                _starvationTask = Task.Run(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        ThreadPool.GetAvailableThreads(out var workers, out _);
                        var blockers = Math.Max(1, workers / 2);
                        for (var i = 0; i < blockers; i++)
                        {
                            ThreadPool.QueueUserWorkItem(_ => Thread.Sleep(250));
                        }

                        Thread.Sleep(100);
                    }
                }, token);
            }
        }

        _logger.LogWarning("Chaos fault enabled: {Kind} ({Detail})", kind, detail);
    }

    /// <inheritdoc />
    public void Clear()
    {
        lock (_gate)
        {
            StopStarvation_NoLock();
            ReleasePressure_NoLock();
            _active = ChaosFaultKind.None;
            _detail = string.Empty;
        }

        _logger.LogInformation("Chaos faults cleared");
    }

    /// <inheritdoc />
    public async ValueTask BeforeNetworkReadAsync(CancellationToken cancellationToken)
    {
        if (!IsPermitted || !IsActive)
        {
            return;
        }

        var kind = ActiveFault;
        if (kind is ChaosFaultKind.NetworkLatency or ChaosFaultKind.SlowDisk)
        {
            await Task.Delay(ResolveLatencyMs(), cancellationToken).ConfigureAwait(false);
        }

        if (kind == ChaosFaultKind.SocketDisconnect && ShouldDisconnectSocket())
        {
            throw new IOException("Chaos: simulated socket disconnect on read.");
        }
    }

    /// <inheritdoc />
    public async ValueTask BeforeNetworkWriteAsync(CancellationToken cancellationToken)
    {
        if (!IsPermitted || !IsActive)
        {
            return;
        }

        var kind = ActiveFault;
        if (kind == ChaosFaultKind.PacketLoss && ShouldDropPacket())
        {
            throw new IOException("Chaos: simulated packet loss on write.");
        }

        if (kind is ChaosFaultKind.NetworkLatency)
        {
            await Task.Delay(ResolveLatencyMs(), cancellationToken).ConfigureAwait(false);
        }

        if (kind == ChaosFaultKind.SocketDisconnect && ShouldDisconnectSocket())
        {
            throw new IOException("Chaos: simulated socket disconnect on write.");
        }
    }

    /// <inheritdoc />
    public async ValueTask BeforeDiskWriteAsync(CancellationToken cancellationToken)
    {
        if (!IsPermitted || !IsActive)
        {
            return;
        }

        var kind = ActiveFault;
        if (kind == ChaosFaultKind.DiskFull)
        {
            throw new IOException("Chaos: simulated disk full.");
        }

        if (kind == ChaosFaultKind.SlowDisk)
        {
            await Task.Delay(ResolveLatencyMs(defaultMs: 200), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask BeforeSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!IsPermitted || !IsActive)
        {
            return;
        }

        if (ActiveFault == ChaosFaultKind.SnapshotInterruption)
        {
            throw new InvalidOperationException("Chaos: simulated snapshot interruption.");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool ShouldDropPacket()
    {
        if (!IsPermitted || ActiveFault != ChaosFaultKind.PacketLoss)
        {
            return false;
        }

        // Drop every 3rd packet by default.
        var n = Interlocked.Increment(ref _packetCounter);
        return n % 3 == 0;
    }

    /// <inheritdoc />
    public bool ShouldCorruptAof()
        => IsPermitted && ActiveFault == ChaosFaultKind.AofCorruption;

    /// <inheritdoc />
    public bool ShouldDisconnectSocket()
        => IsPermitted && ActiveFault == ChaosFaultKind.SocketDisconnect;

    /// <inheritdoc />
    public void Dispose()
    {
        Clear();
        _starvationCts?.Dispose();
    }

    private int ResolveLatencyMs(int defaultMs = 50)
    {
        if (int.TryParse(ActiveDetail, out var ms) && ms > 0)
        {
            return Math.Min(ms, 5_000);
        }

        return defaultMs;
    }

    private void StopStarvation_NoLock()
    {
        try
        {
            _starvationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore.
        }

        _starvationCts?.Dispose();
        _starvationCts = null;
        _starvationTask = null;
    }

    private void ReleasePressure_NoLock()
    {
        while (_pressure.TryTake(out _))
        {
            // Drop references for GC.
        }
    }
}
