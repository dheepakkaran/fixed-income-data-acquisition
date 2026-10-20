using System.Net;
using System.Net.Http.Headers;
using DataAcquisition.Core.Abstractions;
using DataAcquisition.Providers.TreasuryYields.Model;
using DataAcquisition.Providers.TreasuryYields.Storage;
using DataAcquisition.Providers.TreasuryYields.Validation;
using DataAcquisition.Storage;
using DataAcquisition.Storage.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DataAcquisition.Providers.TreasuryYields;

/// <summary>
/// Registers the Treasury provider. The whole cost of a new provider and a new asset class:
/// this one call plus a configuration entry.
/// </summary>
public static class ServiceCollectionExtensions
{
    public const string UserAgentVariable = "TREASURY_USER_AGENT";

    public static IServiceCollection AddTreasuryYieldsProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<TreasuryYieldsOptions>(options =>
        {
            configuration.GetSection(TreasuryYieldsOptions.SectionName).Bind(options);

            var fromEnvironment = Environment.GetEnvironmentVariable(UserAgentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                options.UserAgent = fromEnvironment;
            }
        });

        services.AddHttpClient<TreasuryYieldsProvider>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<TreasuryYieldsOptions>>().Value;

            // Added without validation for the same reason as the SEC client: the
            // "Name email@domain" form is not a valid RFC 9110 product token.
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        .AddStandardResilienceHandler(resilience =>
        {
            // This endpoint is slow by nature: it generates a whole calendar year of the
            // curve per request, and time-to-first-byte was measured at 19.5 s for 2026
            // (310 KB). The standard handler's 10 s attempt timeout cuts that off every
            // time, so the first run failed both sources with a timeout rather than
            // anything to do with the data.
            resilience.AttemptTimeout.Timeout = TimeSpan.FromSeconds(90);
            resilience.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
            resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(10);
        });

        services.TryAddSingleton<TreasuryYieldValidator>();

        services.AddCopyBulkLoader<TreasuryParYield, TreasuryParYieldTableMap>();

        services.AddSingleton<IDataProvider>(sp => ActivatorUtilities.CreateInstance<TreasuryYieldsProvider>(
            sp,
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(TreasuryYieldsProvider)),
            sp.GetRequiredService<RawFileCacheFactory>().ForProvider("TreasuryYields")));

        return services;
    }
}
