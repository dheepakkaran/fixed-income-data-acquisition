using DataAcquisition.Core.Abstractions;
using DataAcquisition.Storage.Caching;
using DataAcquisition.Storage.Loading;
using DataAcquisition.Storage.Migrations;
using DataAcquisition.Storage.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DataAcquisition.Storage;

/// <summary>Container wiring for Postgres storage.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Environment variable that overrides the configured connection string, for CI and the production run.</summary>
    public const string ConnectionStringVariable = "FIDA_CONNECTION_STRING";

    public static IServiceCollection AddDataAcquisitionStorage(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<StorageOptions>(options =>
        {
            configuration.GetSection(StorageOptions.SectionName).Bind(options);

            var fromEnvironment = Environment.GetEnvironmentVariable(ConnectionStringVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                options.ConnectionString = fromEnvironment;
            }
        });

        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            var builder = new NpgsqlDataSourceBuilder(options.ConnectionString);
            return builder.Build();
        });

        services.TryAddSingleton<DatabaseMigrator>();
        services.TryAddSingleton<IngestionRunStore>();
        services.TryAddSingleton<IIngestionRunStore>(sp => sp.GetRequiredService<IngestionRunStore>());
        services.TryAddSingleton<RawFileCacheFactory>();

        return services;
    }

    /// <summary>
    /// Register the COPY loader as the record sink for <typeparamref name="TRecord"/>.
    /// A provider calls this with its own table map.
    /// </summary>
    /// <typeparam name="TRecord">Record type being written.</typeparam>
    /// <typeparam name="TMap">Table map describing the target table.</typeparam>
    public static IServiceCollection AddCopyBulkLoader<TRecord, TMap>(this IServiceCollection services)
        where TMap : class, IPostgresTableMap<TRecord>
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IPostgresTableMap<TRecord>, TMap>();
        services.TryAddSingleton<IBulkLoader<TRecord>, CopyBulkLoader<TRecord>>();
        services.TryAddSingleton<IRecordSink<TRecord>>(sp => sp.GetRequiredService<IBulkLoader<TRecord>>());

        return services;
    }
}
