using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DataAcquisition.Reports;

/// <summary>Container wiring for the report.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPoolReport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<PoolReportRepository>();
        services.TryAddSingleton<ReportGenerator>();

        return services;
    }
}
