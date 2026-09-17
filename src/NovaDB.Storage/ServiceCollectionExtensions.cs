using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NovaDB.Configuration;
using NovaDB.Storage.Eviction;
using NovaDB.Storage.Expiration;

namespace NovaDB.Storage;

/// <summary>
/// Dependency injection extensions for NovaDB storage services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the in-memory storage engine, expiration scheduler, and eviction policy.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection AddNovaDbStorage(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ConfigurableEvictionPolicy>(sp =>
        {
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NovaDbOptions>>().Value;
            return new ConfigurableEvictionPolicy(options.EvictionPolicy);
        });
        services.AddSingleton<IEvictionPolicy>(sp => sp.GetRequiredService<ConfigurableEvictionPolicy>());

        services.AddSingleton<IStorageMetrics>(_ => NullStorageMetrics.Instance);
        services.AddSingleton<MemoryStorageEngine>();
        services.AddSingleton<IStorageEngine>(sp => sp.GetRequiredService<MemoryStorageEngine>());
        services.AddSingleton<TimingWheelExpirationScheduler>();
        services.AddSingleton<IExpirationScheduler>(sp => sp.GetRequiredService<TimingWheelExpirationScheduler>());
        services.AddHostedService(sp => sp.GetRequiredService<TimingWheelExpirationScheduler>());

        return services;
    }
}
