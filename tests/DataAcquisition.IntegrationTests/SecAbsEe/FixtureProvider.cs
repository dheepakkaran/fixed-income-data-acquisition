using System.Runtime.CompilerServices;
using DataAcquisition.Core.Abstractions;
using DataAcquisition.Core.Model;
using DataAcquisition.Core.Pipeline;
using DataAcquisition.Core.Validation;
using DataAcquisition.Providers.SecAbsEe.Model;
using DataAcquisition.Providers.SecAbsEe.Parsing;
using DataAcquisition.Providers.SecAbsEe.Validation;
using Microsoft.Extensions.Logging;

namespace DataAcquisition.IntegrationTests.SecAbsEe;

/// <summary>
/// The real SEC provider's parsing, validation and loading, reading a local fixture instead
/// of calling EDGAR. Only discovery and fetch are stubbed, so the rest of the pipeline under
/// test is production code.
/// </summary>
public sealed class FixtureProvider(
    string fixturePath,
    string trustCik,
    DateOnly period,
    IIngestionRunStore runStore,
    IRecordSink<AutoLoanRecord> sink,
    ILogger<FixtureProvider> logger,
    IPipelineObserver? observer = null)
    : DataProviderBase<AutoLoanRecord>(runStore, sink, logger, observer)
{
    private readonly AutoLoanValidator _validator = new();

    public override string Name => "SecAbsEe";

    public override string AssetClass => AssetClasses.AutoLoanAbs;

    /// <summary>Small enough that a 200-record fixture exercises several batches.</summary>
    protected override int BatchSize => 64;

    public DataSource Source { get; } = new(
        SourceId: Path.GetFileName(fixturePath),
        Uri: new Uri("https://www.sec.gov/Archives/edgar/data/fixture/" + Path.GetFileName(fixturePath)),
        ReportingPeriod: period,
        SubjectId: trustCik,
        Label: $"Fixture {trustCik} {period:yyyy-MM}");

    public override Task<IReadOnlyList<DataSource>> DiscoverAsync(
        IngestionOptions options,
        CancellationToken cancellationToken)
    {
        _validator.Reset();
        return Task.FromResult<IReadOnlyList<DataSource>>([Source]);
    }

    public override Task<FetchedSource> FetchAsync(DataSource source, CancellationToken cancellationToken)
    {
        var info = new FileInfo(fixturePath);
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = info.OpenRead();
        var hash = Convert.ToHexStringLower(sha.ComputeHash(stream));

        return Task.FromResult(new FetchedSource(source, fixturePath, hash, info.Length, FromCache: true));
    }

    public override async IAsyncEnumerable<AutoLoanRecord> ParseAsync(
        FetchedSource source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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
