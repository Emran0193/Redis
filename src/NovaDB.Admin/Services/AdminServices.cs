using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using NovaDB.Contracts.Admin;

namespace NovaDB.Admin.Services;

/// <summary>SignalR hub for live Admin updates.</summary>
public sealed class LiveHub : Hub
{
}

/// <summary>In-memory audit trail for destructive Admin actions.</summary>
public interface IAdminAuditLog
{
    /// <summary>Records an audited action.</summary>
    void Record(string actor, string action, string detail);

    /// <summary>Returns recent audit entries (newest first).</summary>
    IReadOnlyList<AdminAuditEntry> Recent(int take = 100);
}

/// <summary>One audit record.</summary>
/// <param name="Utc">Timestamp.</param>
/// <param name="Actor">User name.</param>
/// <param name="Action">Action verb.</param>
/// <param name="Detail">Free-form detail.</param>
public sealed record AdminAuditEntry(DateTimeOffset Utc, string Actor, string Action, string Detail);

/// <summary>Ring-buffer audit log with optional file append.</summary>
public sealed class AdminAuditLog : IAdminAuditLog
{
    private readonly ConcurrentQueue<AdminAuditEntry> _entries = new();
    private readonly string? _filePath;
    private readonly ILogger<AdminAuditLog> _logger;

    /// <summary>Creates the audit log.</summary>
    public AdminAuditLog(IConfiguration config, ILogger<AdminAuditLog> logger)
    {
        _logger = logger;
        var dir = config["Admin:AuditDirectory"];
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "admin-audit.log");
        }
    }

    /// <inheritdoc />
    public void Record(string actor, string action, string detail)
    {
        var entry = new AdminAuditEntry(DateTimeOffset.UtcNow, actor, action, detail);
        _entries.Enqueue(entry);
        while (_entries.Count > 1000)
        {
            _entries.TryDequeue(out _);
        }

        _logger.LogInformation("AUDIT {Actor} {Action} {Detail}", actor, action, detail);
        if (_filePath is not null)
        {
            File.AppendAllText(
                _filePath,
                $"{entry.Utc:o}\t{entry.Actor}\t{entry.Action}\t{entry.Detail}{Environment.NewLine}");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AdminAuditEntry> Recent(int take = 100)
        => _entries.Reverse().Take(take).ToArray();
}

/// <summary>Structured log buffer for the Logs page.</summary>
public sealed class AdminLogBuffer
{
    private readonly ConcurrentQueue<AdminLogEntry> _entries = new();

    /// <summary>Appends a log line.</summary>
    public void Add(LogLevel level, string message)
    {
        _entries.Enqueue(new AdminLogEntry(DateTimeOffset.UtcNow, level, message));
        while (_entries.Count > 2000)
        {
            _entries.TryDequeue(out _);
        }
    }

    /// <summary>Returns recent log lines.</summary>
    public IReadOnlyList<AdminLogEntry> Recent(int take = 200)
        => _entries.Reverse().Take(take).ToArray();
}

/// <summary>One buffered log line.</summary>
public sealed record AdminLogEntry(DateTimeOffset Utc, LogLevel Level, string Message);

/// <summary>MudBlazor theme toggle state.</summary>
public sealed class ThemeState
{
    /// <summary>Gets or sets whether dark mode is enabled.</summary>
    public bool IsDarkMode { get; set; } = true;

    /// <summary>Raised when theme changes.</summary>
    public event Action? Changed;

    /// <summary>Toggles dark mode.</summary>
    public void Toggle()
    {
        IsDarkMode = !IsDarkMode;
        Changed?.Invoke();
    }
}

/// <summary>Plain DTO for SignalR metrics fan-out (protobuf types do not JSON round-trip reliably).</summary>
public sealed record LiveMetricsDto(
    long MemoryBytes,
    long MemoryLimitBytes,
    long ConnectedClients,
    long KeyCount,
    long CacheHits,
    long CacheMisses,
    double HitRatio,
    long Evictions,
    long ExpiredKeys);

/// <summary>Plain DTO for SignalR health fan-out.</summary>
public sealed record LiveHealthDto(bool Ready, string Status, string Description, long KeyCount, long MemoryBytes);

/// <summary>Plain DTO for SignalR client list fan-out.</summary>
public sealed record LiveClientDto(string ConnectionId, string RemoteAddress, bool Authenticated, bool Subscribed);

/// <summary>
/// Hosted feeder: pulls gRPC snapshots and pushes to SignalR (browser does not poll).
/// </summary>
public sealed class LiveTelemetryFeeder : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IHubContext<LiveHub> _hub;
    private readonly AdminLogBuffer _logs;

    /// <summary>Creates the feeder.</summary>
    public LiveTelemetryFeeder(IServiceProvider services, IHubContext<LiveHub> hub, AdminLogBuffer logs)
    {
        _services = services;
        _hub = hub;
        _logs = logs;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _services.CreateAsyncScope();
                var metrics = scope.ServiceProvider.GetRequiredService<MetricsService.MetricsServiceClient>();
                var health = scope.ServiceProvider.GetRequiredService<HealthService.HealthServiceClient>();
                var clients = scope.ServiceProvider.GetRequiredService<ClientService.ClientServiceClient>();

                var snapshot = await metrics.GetSnapshotAsync(new Empty(), cancellationToken: stoppingToken);
                var healthSnap = await health.GetHealthAsync(new Empty(), cancellationToken: stoppingToken);
                var clientList = await clients.ListClientsAsync(new Empty(), cancellationToken: stoppingToken);

                var liveMetrics = new LiveMetricsDto(
                    snapshot.MemoryBytes,
                    snapshot.MemoryLimitBytes,
                    snapshot.ConnectedClients,
                    snapshot.KeyCount,
                    snapshot.CacheHits,
                    snapshot.CacheMisses,
                    snapshot.HitRatio,
                    snapshot.Evictions,
                    snapshot.ExpiredKeys);
                var liveHealth = new LiveHealthDto(
                    healthSnap.Ready,
                    healthSnap.Status,
                    healthSnap.Description,
                    healthSnap.KeyCount,
                    healthSnap.MemoryBytes);
                var liveClients = clientList.Clients
                    .Select(c => new LiveClientDto(c.ConnectionId, c.RemoteAddress, c.Authenticated, c.Subscribed))
                    .ToArray();

                await _hub.Clients.All.SendAsync("metrics", liveMetrics, stoppingToken).ConfigureAwait(false);
                await _hub.Clients.All.SendAsync("health", liveHealth, stoppingToken).ConfigureAwait(false);
                await _hub.Clients.All.SendAsync("clients", liveClients, stoppingToken).ConfigureAwait(false);

                var diagnostics = scope.ServiceProvider.GetRequiredService<DiagnosticsService.DiagnosticsServiceClient>();
                var memory = await diagnostics.GetMemoryDiagnosticsAsync(new Empty(), cancellationToken: stoppingToken);
                await _hub.Clients.All.SendAsync("diagnostics", memory, stoppingToken).ConfigureAwait(false);

                _logs.Add(LogLevel.Debug, $"telemetry tick keys={snapshot.KeyCount} clients={snapshot.ConnectedClients}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logs.Add(LogLevel.Warning, $"telemetry feed error: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
    }
}
