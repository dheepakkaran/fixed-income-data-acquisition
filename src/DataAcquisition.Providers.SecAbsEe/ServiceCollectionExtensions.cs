using System.Net;
using System.Net.Http.Headers;
using DataAcquisition.Core.Abstractions;
using DataAcquisition.Providers.SecAbsEe.Http;
using DataAcquisition.Providers.SecAbsEe.Model;
using DataAcquisition.Providers.SecAbsEe.Storage;
using DataAcquisition.Providers.SecAbsEe.Validation;
using DataAcquisition.Storage;
using DataAcquisition.Storage.Caching;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataAcquisition.Providers.SecAbsEe;

/// <summary>
/// Registers the SEC ABS-EE provider. This is the whole cost of adding a provider: one
/// call here plus a configuration entry. Nothing in <c>DataAcquisition.Core</c> changes.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Overrides the configured User-Agent, so a fork does not file requests under someone else's name.</summary>
    public const string UserAgentVariable = "SEC_USER_AGENT";

    public static IServiceCollection AddSecAbsEeProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<SecAbsEeOptions>(options =>
        {
            configuration.GetSection(SecAbsEeOptions.SectionName).Bind(options);

            var fromEnvironment = Environment.GetEnvironmentVariable(UserAgentVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                options.UserAgent = fromEnvironment;
            }
        });

        services.AddHttpClient<EdgarClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<SecAbsEeOptions>>().Value;

            // SEC fair access requires a declared User-Agent naming a real person and a
            // reachable address, in the form "Name email@domain". A browser User-Agent is
            // answered with 403.
            //
            // Added without validation on purpose: the User-Agent grammar in RFC 9110 is a
            // list of product tokens and comments, and a bare email address is neither, so
            // the strongly typed UserAgent.ParseAdd rejects exactly the format SEC asks for.
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.UserAgent);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("deflate"));

            // Exhibits run to 160 MB; the default 100 s would abandon a healthy download.
            client.Timeout = TimeSpan.FromMinutes(15);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        .AddHttpMessageHandler(sp => new SecRateLimitingHandler(
            sp.GetRequiredService<IOptions<SecAbsEeOptions>>().Value.MaxRequestsPerSecond,
            sp.GetRequiredService<ILogger<SecRateLimitingHandler>>()))
        .AddStandardResilienceHandler(resilience =>
        {
            // The pipeline's own attempt timeout must not cut a large download short.
            resilience.AttemptTimeout.Timeout = TimeSpan.FromMinutes(10);
            resilience.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(20);
            resilience.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(20);
        });

        services.TryAddSingleton<AutoLoanValidator>();

        // Each provider gets a cache rooted in its own directory, keyed by accession number.
        services.TryAddSingleton<IRawFileCache>(sp =>
            sp.GetRequiredService<RawFileCacheFactory>().ForProvider("SecAbsEe"));

        services.AddCopyBulkLoader<AutoLoanRecord, AutoLoanTableMap>();

        services.AddSingleton<IDataProvider, SecAbsEeProvider>();

        return services;
    }
}
