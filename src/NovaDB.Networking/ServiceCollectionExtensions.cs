using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NovaDB.Monitoring;

namespace NovaDB.Networking;

/// <summary>
/// Dependency injection extensions for NovaDB TCP networking.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers TCP connection management and the RESP server hosted service.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <remarks>
    /// An <see cref="ICommandProcessor"/> implementation must be registered separately
    /// (typically by the server or commands layer).
    /// </remarks>
    public static IServiceCollection AddNovaDbNetworking(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<INovaDbMetrics>(_ => NullNovaDbMetrics.Instance);
        services.AddSingleton<TlsCertificateProvider>();
        services.AddSingleton<ConnectionManager>();
        services.AddSingleton<ConnectionRateLimiter>();
        services.AddHostedService<TcpServerHostedService>();

        return services;
    }
}
