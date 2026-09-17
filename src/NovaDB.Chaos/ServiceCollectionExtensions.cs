using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NovaDB.Chaos;

/// <summary>DI registration for chaos fault injection.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IChaosFaultEngine"/>. Faults remain inert unless
    /// <c>NovaDB:ChaosEnabled</c> is true and the host environment is Development.
    /// </summary>
    public static IServiceCollection AddNovaDbChaos(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<ChaosFaultEngine>();
        services.TryAddSingleton<IChaosFaultEngine>(sp => sp.GetRequiredService<ChaosFaultEngine>());
        return services;
    }
}
