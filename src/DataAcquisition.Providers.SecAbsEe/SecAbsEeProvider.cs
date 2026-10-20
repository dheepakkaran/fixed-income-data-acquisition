using System.Runtime.CompilerServices;
using DataAcquisition.Core.Abstractions;
using DataAcquisition.Core.Model;
using DataAcquisition.Core.Pipeline;
using DataAcquisition.Core.Validation;
using DataAcquisition.Providers.SecAbsEe.Http;
using DataAcquisition.Providers.SecAbsEe.Model;
using DataAcquisition.Providers.SecAbsEe.Parsing;
using DataAcquisition.Providers.SecAbsEe.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataAcquisition.Providers.SecAbsEe;

/// <summary>
/// Loan-level auto loan ABS data from SEC EDGAR Form ABS-EE, exhibit EX-102.
/// <para>
/// Supplies only the four pipeline steps; discovery order, idempotency, batching,
/// transaction boundaries and run accounting all stay in
/// <see cref="DataProviderBase{TRecord}"/>.
/// </para>
/// </summary>
public sealed class SecAbsEeProvider : DataProviderBase<AutoLoanRecord>
{
    private readonly EdgarClient _edgar;
    private readonly IRawFileCache _cache;
    private readonly AutoLoanValidator _validator;
    private readonly SecAbsEeOptions _options;

    public SecAbsEeProvider(
        EdgarClient edgar,
        IRawFileCache cache,
        AutoLoanValidator validator,
        IOptions<SecAbsEeOptions> options,
        IIngestionRunStore runStore,
        IRecordSink<AutoLoanRecord> sink,
        ILogger<SecAbsEeProvider> logger,
        IPipelineObserver? observer = null)
        : base(runStore, sink, logger, observer)
    {
        _edgar = edgar ?? throw new ArgumentNullException(nameof(edgar));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    }

    public override string Name => "SecAbsEe";

    public override string AssetClass => AssetClasses.AutoLoanAbs;

    /// <summary>
    /// Discover the most recent ABS-EE filings for each configured trust and locate the
    /// EX-102 exhibit in each. The exhibit's document name differs per filing agent, so it
    /// is read from the filing index rather than guessed.
    /// </summary>
    public override async Task<IReadOnlyList<DataSource>> DiscoverAsync(
        IngestionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        // One run, one set of seen keys. This provider is a singleton, so a second run must
        // not inherit the first run's duplicate-detection state.
        _validator.Reset();

        var wanted = options.Subjects.Count > 0
            ? _options.Trusts.Where(t => options.Subjects.Contains(EdgarClient.TrimCik(t.Cik))
                                      || options.Subjects.Contains(t.Cik)).ToList()
            : [.. _options.Trusts];

        if (wanted.Count == 0)
        {
            Logger.LogWarning("No trusts configured under {Section}:Trusts", SecAbsEeOptions.SectionName);
            return [];
        }

        var periods = options.MaxPeriods > 0 ? options.MaxPeriods : _options.MaxPeriods;
        var sources = new List<DataSource>();

        foreach (var trust in wanted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var filings = await _edgar
                .GetAbsEeFilingsAsync(trust.Cik, cancellationToken)
                .ConfigureAwait(false);

            foreach (var filing in filings.Take(periods))
            {
                var exhibit = await _edgar
                    .FindAssetDataFileAsync(trust.Cik, filing, cancellationToken)
                    .ConfigureAwait(false);

                if (exhibit is not { } found)
                {
                    continue;
                }

                if (found.SizeBytes > _options.MaxExhibitBytes)
                {
                    Logger.LogWarning(
                        "Skipping {Trust} {Period}: exhibit is {Size:N0} bytes, over the {Max:N0} byte ceiling",
                        trust.Name,
                        filing.ReportingPeriod,
                        found.SizeBytes,
                        _options.MaxExhibitBytes);
                    continue;
                }

                sources.Add(new DataSource(
                    SourceId: $"{filing.AccessionCompact}-{found.FileName}",
                    Uri: new Uri(
                        $"https://www.sec.gov/Archives/edgar/data/{EdgarClient.TrimCik(trust.Cik)}/"
                        + $"{filing.AccessionCompact}/{found.FileName}"),
                    ReportingPeriod: filing.ReportingPeriod,
                    SubjectId: EdgarClient.TrimCik(trust.Cik),
                    Label: $"{trust.Name} {filing.ReportingPeriod:yyyy-MM}"));
            }
        }

        return sources;
    }

    /// <summary>
    /// The cache key is the source id, which is the accession number plus the document
    /// name, so the on-disk cache is addressed exactly as SEC addresses the file.
    /// </summary>
    protected override async Task<string?> TryGetCachedHashAsync(
        DataSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        return _cache.TryGet(source.SourceId, out var path)
            ? await _cache.ComputeHashAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public override Task<FetchedSource> FetchAsync(
        DataSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        return _edgar.FetchExhibitAsync(source, source.SourceId, cancellationToken);
    }

    public override async IAsyncEnumerable<AutoLoanRecord> ParseAsync(
        FetchedSource source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var trustCik = source.Source.SubjectId
            ?? throw new InvalidOperationException(
                $"Source {source.Source.SourceId} has no trust CIK, so its records cannot be keyed.");

        await foreach (var record in AutoLoanXmlParser
            .ParseFileAsync(source.LocalPath, trustCik, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return record;
        }
    }

    public override Task<ValidationResult> ValidateAsync(
        IReadOnlyList<AutoLoanRecord> batch,
        CancellationToken cancellationToken) => Task.FromResult(_validator.Validate(batch));
}
