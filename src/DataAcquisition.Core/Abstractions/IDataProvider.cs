using DataAcquisition.Core.Model;
using DataAcquisition.Core.Validation;

namespace DataAcquisition.Core.Abstractions;

/// <summary>
/// Non-generic handle the registry and the CLI hold. Record-shaped work lives on
/// <see cref="IDataProvider{TRecord}"/>; this face exists so providers for different
/// record types can sit in one collection.
/// </summary>
public interface IDataProvider
{
    /// <summary>Stable key used in configuration (<c>Providers:&lt;Name&gt;:Enabled</c>) and run rows.</summary>
    string Name { get; }

    /// <summary>Asset class this provider supplies, from <see cref="AssetClasses"/>.</summary>
    string AssetClass { get; }

    /// <summary>Run the full pipeline over every source this provider discovers.</summary>
    Task<IngestionRunSummary> IngestAsync(IngestionOptions options, CancellationToken cancellationToken);
}

/// <summary>
/// The pipeline steps a provider implements. <see cref="DataProviderBase{TRecord}"/>
/// sequences these; a provider overrides only the ones that differ.
/// </summary>
/// <typeparam name="TRecord">Record type this provider yields.</typeparam>
public interface IDataProvider<TRecord> : IDataProvider
{
    /// <summary>Find the sources available upstream, newest first.</summary>
    Task<IReadOnlyList<DataSource>> DiscoverAsync(
        IngestionOptions options,
        CancellationToken cancellationToken);

    /// <summary>Bring one source's bytes to local disk, preferring the on-disk cache.</summary>
    Task<FetchedSource> FetchAsync(DataSource source, CancellationToken cancellationToken);

    /// <summary>
    /// Stream records out of a fetched file. Implementations must not hold the whole
    /// document in memory: ABS-EE exhibits run to 150 MB.
    /// </summary>
    IAsyncEnumerable<TRecord> ParseAsync(FetchedSource source, CancellationToken cancellationToken);

    /// <summary>
    /// Check one batch of parsed records. Called repeatedly as the stream advances and the
    /// results merged, so a provider sees bounded batches rather than the whole file.
    /// </summary>
    Task<ValidationResult> ValidateAsync(
        IReadOnlyList<TRecord> batch,
        CancellationToken cancellationToken);
}
