using DataAcquisition.Core.Pipeline;
using DataAcquisition.Core.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataAcquisition.Core;

/// <summary>Container wiring for the framework core.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the registry and the default enablement policy. Providers register
    /// themselves against <c>IDataProvider</c> and are picked up here.
    /// </summary>
    public static IServiceCollection AddDataAcquisitionCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IProviderEnablement, ConfigurationProviderEnablement>();
        services.TryAddSingleton<IPipelineObserver>(NullPipelineObserver.Instance);
        services.TryAddSingleton<ProviderRegistry>();

        return services;
    }
}
