using System.Runtime.CompilerServices;
using DataAcquisition.Core.Abstractions;
using DataAcquisition.Core.Model;
using DataAcquisition.Core.Pipeline;
using DataAcquisition.Core.Validation;
using Microsoft.Extensions.Logging;

namespace DataAcquisition.UnitTests.Fakes;

/// <summary>
/// A provider for a made-up asset class, written only against the published Core
/// abstractions. Its existence is the test that a new provider needs no Core change:
/// it subclasses <see cref="DataProviderBase{TRecord}"/>, overrides the four steps and
/// nothing else.
/// </summary>
public sealed class FakeWidgetProvider : DataProviderBase<WidgetRecord>
{
    private readonly IReadOnlyList<DataSource> _sources;
    private readonly IReadOnlyList<WidgetRecord> _records;
    private readonly string _sha256;
    private readonly string? _cachedHash;

    public FakeWidgetProvider(
        IIngestionRunStore runStore,
        IRecordSink<WidgetRecord> sink,
        ILogger<FakeWidgetProvider> logger,
        IReadOnlyList<DataSource> sources,
        IReadOnlyList<WidgetRecord> records,
        string sha256 = "fake-hash",
        string? cachedHash = null,
        IPipelineObserver? observer = null)
        : base(runStore, sink, logger, observer)
    {
        _sources = sources;
        _records = records;
        _sha256 = sha256;
        _cachedHash = cachedHash;
    }

    public override string Name => "FakeWidgets";

    public override string AssetClass => "Widgets";

    public int FetchCount { get; private set; }

    public int ParseCount { get; private set; }

    /// <summary>Small so batching behaviour is exercised by a handful of records.</summary>
    protected override int BatchSize => 2;

    public override Task<IReadOnlyList<DataSource>> DiscoverAsync(
        IngestionOptions options,
        CancellationToken cancellationToken) => Task.FromResult(_sources);

    protected override Task<string?> TryGetCachedHashAsync(
        DataSource source,
        CancellationToken cancellationToken) => Task.FromResult(_cachedHash);

    public override Task<FetchedSource> FetchAsync(
        DataSource source,
        CancellationToken cancellationToken)
    {
        FetchCount++;
        return Task.FromResult(new FetchedSource(source, $"/tmp/{source.SourceId}", _sha256, 1024, false));
    }

    public override async IAsyncEnumerable<WidgetRecord> ParseAsync(
        FetchedSource source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ParseCount++;
        foreach (var record in _records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
            await Task.Yield();
        }
    }

    public override Task<ValidationResult> ValidateAsync(
        IReadOnlyList<WidgetRecord> batch,
        CancellationToken cancellationToken)
    {
        var result = ValidationResult.Empty();
        result.Checked(batch.Count);

        foreach (var record in batch)
        {
            if (string.IsNullOrWhiteSpace(record.Key))
            {
                result.Fail(ValidationRules.RequiredField, ValidationSeverity.Error, "Key is blank");
            }

            if (record.Value < 0)
            {
                result.Fail(
                    ValidationRules.ValueRange,
                    ValidationSeverity.Error,
                    $"Value {record.Value} is negative for {record.Key}");
            }
        }

        return Task.FromResult(result);
    }
}
