using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace NovaDB.Configuration;

/// <summary>
/// DI extensions for NovaDB configuration.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="NovaDbOptions"/> from configuration and registers it with the options pattern.
    /// </summary>
    public static IServiceCollection AddNovaDbConfiguration(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<NovaDbOptions>()
            .Bind(configuration.GetSection(NovaDbOptions.SectionName))
            .Validate(o => o.Port is > 0 and < 65536, "Port must be between 1 and 65535.")
            .Validate(o => o.MaxConnections > 0, "MaxConnections must be positive.")
            .Validate(o => o.ShardCount > 0, "ShardCount must be positive.")
            .Validate(o => o.MaxBulkBytes > 0, "MaxBulkBytes must be positive.")
            .Validate(o => o.MaxArrayLength > 0, "MaxArrayLength must be positive.")
            .Validate(o => o.MaxLineLength > 0, "MaxLineLength must be positive.")
            .Validate(
                o => o.AllowUnauthenticatedPublicBind
                     || IsLoopbackBind(o.BindAddress)
                     || o.AuthenticationRequired
                     || o.TlsEnabled,
                "Non-loopback BindAddress requires Password and/or TlsEnabled (or AllowUnauthenticatedPublicBind).")
            .ValidateOnStart();

        return services;
    }

    private static bool IsLoopbackBind(string bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress))
        {
            return true;
        }

        return bindAddress is "127.0.0.1" or "::1" or "localhost"
            || string.Equals(bindAddress, "loopback", StringComparison.OrdinalIgnoreCase);
    }
}
