using Microsoft.Extensions.DependencyInjection;

namespace NovaDB.Protocol;

/// <summary>
/// DI registration for protocol services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers protocol helpers (stateless; reserved for future pooled writers).
    /// </summary>
    public static IServiceCollection AddNovaDbProtocol(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
