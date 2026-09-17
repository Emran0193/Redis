using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NovaDB.Core.Hosting;
using NovaDB.Storage;

namespace NovaDB.Monitoring;

/// <summary>
/// Readiness health check: recovery must be complete and storage must be reachable.
/// </summary>
public sealed class NovaDbHealthCheck : IHealthCheck
{
    private readonly IStorageEngine _storage;
    private readonly IDatabaseReadiness? _readiness;
    private readonly ILogger<NovaDbHealthCheck> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="NovaDbHealthCheck"/> class.
    /// </summary>
    public NovaDbHealthCheck(
        IStorageEngine storage,
        ILogger<NovaDbHealthCheck> logger,
        IDatabaseReadiness? readiness = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _readiness = readiness;
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (_readiness is { IsReady: false })
            {
                return Task.FromResult(
                    HealthCheckResult.Unhealthy("NovaDB recovery has not completed; RESP accept is not ready."));
            }

            var keyCount = _storage.KeyCount;
            var memoryBytes = _storage.EstimatedMemoryBytes;
            return Task.FromResult(
                HealthCheckResult.Healthy($"NovaDB is ready. Keys={keyCount}, MemoryBytes={memoryBytes}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NovaDB health check failed");
            return Task.FromResult(HealthCheckResult.Unhealthy("NovaDB storage health check failed.", ex));
        }
    }
}
