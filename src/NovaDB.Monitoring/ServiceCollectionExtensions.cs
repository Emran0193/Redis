using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NovaDB.Persistence;
using NovaDB.Storage;
using OpenTelemetry.Metrics;

namespace NovaDB.Monitoring;

/// <summary>
/// Dependency injection extensions for NovaDB monitoring services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers NovaDB metrics, health checks, and OpenTelemetry meter instrumentation.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddNovaDbMonitoring(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<NovaDbMetrics>();
        services.AddSingleton<INovaDbMetrics>(sp => sp.GetRequiredService<NovaDbMetrics>());
        services.Replace(ServiceDescriptor.Singleton<IStorageMetrics>(sp =>
            new StorageMetricsAdapter(sp.GetRequiredService<INovaDbMetrics>())));
        services.Replace(ServiceDescriptor.Singleton<IAofMetrics>(sp =>
            new AofMetricsAdapter(sp.GetRequiredService<INovaDbMetrics>())));
        services.AddSingleton<NovaDbHealthCheck>();

        services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(NovaDbMetrics.MeterName);
                metrics.AddPrometheusExporter();
            });

        return services;
    }
}
