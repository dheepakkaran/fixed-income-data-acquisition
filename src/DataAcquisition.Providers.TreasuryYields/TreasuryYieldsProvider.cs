using System.Runtime.CompilerServices;
using DataAcquisition.Core.Abstractions;
using DataAcquisition.Core.Model;
using DataAcquisition.Core.Pipeline;
using DataAcquisition.Core.Validation;
using DataAcquisition.Providers.TreasuryYields.Model;
using DataAcquisition.Providers.TreasuryYields.Parsing;
using DataAcquisition.Providers.TreasuryYields.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataAcquisition.Providers.TreasuryYields;

/// <summary>
/// U.S. Treasury daily par yield curve rates.
/// <para>
/// A second asset class on the same framework. It subclasses the same
/// <see cref="DataProviderBase{TRecord}"/>, overrides the same four steps, registers through
/// the same DI call and is switched by the same configuration key. Nothing in
/// <c>DataAcquisition.Core</c> was changed to accommodate it.
/// </para>
/// </summary>
public sealed class TreasuryYieldsProvider : DataProviderBase<TreasuryParYield>
{
    private readonly HttpClient _httpClient;
    private readonly IRawFileCache _cache;
    private readonly TreasuryYieldValidator _validator;
    private readonly TreasuryYieldsOptions _options;
    private readonly TimeProvider _timeProvider;

    public TreasuryYieldsProvider(
        HttpClient httpClient,
        IRawFileCache cache,
        TreasuryYieldValidator validator,
        IOptions<TreasuryYieldsOptions> options,
        IIngestionRunStore runStore,
        IRecordSink<TreasuryParYield> sink,
        ILogger<TreasuryYieldsProvider> logger,
        TimeProvider? timeProvider = null,
        IPipelineObserver? observer = null)
        : base(runStore, sink, logger, observer)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override string Name => "TreasuryYields";

    public override string AssetClass => AssetClasses.UsTreasuries;

    /// <summary>Feed endpoint for one calendar year of the daily par yield curve.</summary>
    public static Uri FeedUri(int year) => new(
        "https://home.treasury.gov/resource-center/data-chart-center/interest-rates/pages/xml"
        + $"?data=daily_treasury_yield_curve&field_tdr_date_value={year}");

    /// <summary>
    /// One source per calendar year, newest first. The feed grows through the year, so the
    /// current year's content hash changes with every new business day and a re-run picks up
    /// the additions; a completed year hashes identically and is skipped without a request.
    /// </summary>
    public override Task<IReadOnlyList<DataSource>> DiscoverAsync(
        IngestionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        _validator.Reset();

        var currentYear = _timeProvider.GetUtcNow().Year;
        var years = Math.Max(1, _options.Years);

        var sources = Enumerable.Range(0, years)
            .Select(offset => currentYear - offset)
            .Select(year => new DataSource(
                SourceId: $"daily_treasury_yield_curve-{year}",
                Uri: FeedUri(year),
                ReportingPeriod: new DateOnly(year, 12, 31),
                SubjectId: "UST",
                Label: $"U.S. Treasury par yield curve {year}"))
            .ToArray();

        return Task.FromResult<IReadOnlyList<DataSource>>(sources);
    }

    /// <summary>
    /// Deliberately not overridden to answer from the cache. Unlike an ABS-EE exhibit, which
    /// is immutable once filed, the current year's feed gains a row every business day, so a
    /// cached copy says nothing about whether the upstream has changed.
    /// </summary>
    public override async Task<FetchedSource> FetchAsync(
        DataSource source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        Logger.LogInformation("Downloading {Uri}", source.Uri);

        using var response = await _httpClient
            .GetAsync(source.Uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var content = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        var (path, sha256) = await _cache
            .WriteAsync(source.SourceId, content, cancellationToken)
            .ConfigureAwait(false);

        var bytes = new FileInfo(path).Length;
        Logger.LogInformation("Cached {Source} as {Bytes:N0} bytes", source, bytes);

        return new FetchedSource(source, path, sha256, bytes, FromCache: false);
    }

    public override async IAsyncEnumerable<TreasuryParYield> ParseAsync(
        FetchedSource source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        await foreach (var point in TreasuryYieldXmlParser
            .ParseFileAsync(source.LocalPath, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return point;
        }
    }

    public override Task<ValidationResult> ValidateAsync(
        IReadOnlyList<TreasuryParYield> batch,
        CancellationToken cancellationToken) => Task.FromResult(_validator.Validate(batch));
}
