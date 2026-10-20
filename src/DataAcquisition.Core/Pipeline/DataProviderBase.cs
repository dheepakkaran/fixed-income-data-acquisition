using System.Diagnostics;
using DataAcquisition.Core.Abstractions;
using DataAcquisition.Core.Model;
using DataAcquisition.Core.Validation;
using Microsoft.Extensions.Logging;

namespace DataAcquisition.Core.Pipeline;

/// <summary>
/// Template method for one ingestion pass:
/// discover, skip what is already ingested, fetch through the cache, parse as a stream,
/// validate in batches, load atomically, then record the run.
/// <para>
/// A provider supplies <see cref="IDataProvider{TRecord}.DiscoverAsync"/>,
/// <see cref="IDataProvider{TRecord}.FetchAsync"/>, <see cref="IDataProvider{TRecord}.ParseAsync"/>
/// and <see cref="IDataProvider{TRecord}.ValidateAsync"/>; sequencing, idempotency,
/// transaction boundaries and run accounting stay here.
/// </para>
/// </summary>
/// <typeparam name="TRecord">Record type this provider yields.</typeparam>
public abstract class DataProviderBase<TRecord> : IDataProvider<TRecord>
{
    private readonly IIngestionRunStore _runStore;
    private readonly IRecordSink<TRecord> _sink;
    private readonly IPipelineObserver _observer;

    protected DataProviderBase(
        IIngestionRunStore runStore,
        IRecordSink<TRecord> sink,
        ILogger logger,
        IPipelineObserver? observer = null)
    {
        _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _observer = observer ?? NullPipelineObserver.Instance;
    }

    protected ILogger Logger { get; }

    public abstract string Name { get; }

    public abstract string AssetClass { get; }

    /// <summary>
    /// Records validated and staged per batch. Bounds memory independently of file size:
    /// a 150 MB exhibit and a 1 GB one both hold at most this many records at once.
    /// </summary>
    protected virtual int BatchSize => 10_000;

    public abstract Task<IReadOnlyList<DataSource>> DiscoverAsync(
        IngestionOptions options,
        CancellationToken cancellationToken);

    public abstract Task<FetchedSource> FetchAsync(
        DataSource source,
        CancellationToken cancellationToken);

    public abstract IAsyncEnumerable<TRecord> ParseAsync(
        FetchedSource source,
        CancellationToken cancellationToken);

    public abstract Task<ValidationResult> ValidateAsync(
        IReadOnlyList<TRecord> batch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Hook for a provider that can decide from the cache alone whether a source is
    /// already ingested, avoiding even a conditional request. Returns null when unknown.
    /// </summary>
    protected virtual Task<string?> TryGetCachedHashAsync(
        DataSource source,
        CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public async Task<IngestionRunSummary> IngestAsync(
        IngestionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        await _observer.StageAsync(Name, PipelineStage.Discover, null, cancellationToken)
            .ConfigureAwait(false);

        var sources = await DiscoverAsync(options, cancellationToken).ConfigureAwait(false);
        Logger.LogInformation(
            "Provider {Provider} discovered {Count} source(s)", Name, sources.Count);

        var runs = new List<IngestionRun>(sources.Count);
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            runs.Add(await IngestSourceAsync(source, options, cancellationToken).ConfigureAwait(false));
        }

        return new IngestionRunSummary(Name, runs);
    }

    private async Task<IngestionRun> IngestSourceAsync(
        DataSource source,
        IngestionOptions options,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var run = new IngestionRun
        {
            ProviderName = Name,
            AssetClass = AssetClass,
            SourceId = source.SourceId,
            SubjectId = source.SubjectId,
            ReportingPeriod = source.ReportingPeriod,
            StartedAt = started,
        };

        // Skip check before the network wherever possible. When the raw file is already in
        // the on-disk cache its hash is known locally, so a repeat run costs no request at
        // all. A source seen for the first time has no content hash until it is fetched,
        // so that case is re-checked immediately after the fetch below.
        if (!options.Force)
        {
            var cachedHash = await TryGetCachedHashAsync(source, cancellationToken).ConfigureAwait(false);
            if (cachedHash is not null &&
                await _runStore.IsAlreadyIngestedAsync(Name, source.SourceId, cachedHash, cancellationToken)
                    .ConfigureAwait(false))
            {
                await _observer.StageAsync(Name, PipelineStage.Skip, source, cancellationToken)
                    .ConfigureAwait(false);
                Logger.LogInformation(
                    "Skipping {Source}: already ingested (hash {Hash})", source, Short(cachedHash));

                return run with
                {
                    Status = IngestionRunStatus.Skipped,
                    Sha256 = cachedHash,
                    CompletedAt = DateTimeOffset.UtcNow,
                };
            }
        }

        if (options.DryRun)
        {
            Logger.LogInformation("Dry run: would ingest {Source}", source);
            return run with
            {
                Status = IngestionRunStatus.Skipped,
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }

        FetchedSource fetched;
        try
        {
            await _observer.StageAsync(Name, PipelineStage.Fetch, source, cancellationToken)
                .ConfigureAwait(false);
            fetched = await FetchAsync(source, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Fetch failed for {Source}", source);
            return await FailAsync(run, $"fetch: {ex.Message}", cancellationToken).ConfigureAwait(false);
        }

        if (!options.Force &&
            await _runStore.IsAlreadyIngestedAsync(Name, source.SourceId, fetched.Sha256, cancellationToken)
                .ConfigureAwait(false))
        {
            await _observer.StageAsync(Name, PipelineStage.Skip, source, cancellationToken)
                .ConfigureAwait(false);
            Logger.LogInformation(
                "Skipping {Source}: already ingested (hash {Hash})", source, Short(fetched.Sha256));

            return run with
            {
                Status = IngestionRunStatus.Skipped,
                Sha256 = fetched.Sha256,
                CompletedAt = DateTimeOffset.UtcNow,
            };
        }

        run = await _runStore.StartAsync(run with { Sha256 = fetched.Sha256 }, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var outcome = await StreamIntoSinkAsync(fetched, cancellationToken).ConfigureAwait(false);

            if (outcome.Validation.HasErrors)
            {
                // Nothing was committed: the sink session was disposed without a commit.
                var message =
                    $"validation failed: {outcome.Validation.ErrorCount:N0} error(s) across " +
                    $"{outcome.Validation.RecordsChecked:N0} record(s) [{outcome.Validation.Describe()}]";
                Logger.LogError("Load abandoned for {Source}: {Message}", source, message);

                return await FailAsync(
                    run with
                    {
                        RecordsParsed = outcome.Parsed,
                        ValidationErrors = outcome.Validation.ErrorCount,
                        ValidationWarnings = outcome.Validation.WarningCount,
                    },
                    message,
                    cancellationToken).ConfigureAwait(false);
            }

            await _observer.StageAsync(Name, PipelineStage.Record, source, cancellationToken)
                .ConfigureAwait(false);

            var completed = run with
            {
                Status = IngestionRunStatus.Succeeded,
                CompletedAt = DateTimeOffset.UtcNow,
                RecordsParsed = outcome.Parsed,
                RecordsLoaded = outcome.Loaded,
                ValidationErrors = 0,
                ValidationWarnings = outcome.Validation.WarningCount,
            };

            await _runStore.CompleteAsync(completed, cancellationToken).ConfigureAwait(false);

            Logger.LogInformation(
                "Ingested {Source}: {Parsed:N0} parsed, {Loaded:N0} loaded in {Elapsed} ({Validation})",
                source,
                outcome.Parsed,
                outcome.Loaded,
                completed.Duration,
                outcome.Validation.Describe());

            return completed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Ingestion failed for {Source}", source);
            return await FailAsync(run, ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(long Parsed, long Loaded, ValidationResult Validation)> StreamIntoSinkAsync(
        FetchedSource fetched,
        CancellationToken cancellationToken)
    {
        var validation = ValidationResult.Empty();
        var parsed = 0L;
        var loaded = 0L;
        var stopwatch = Stopwatch.StartNew();

        await using var session = await _sink
            .BeginAsync(fetched.Source, cancellationToken)
            .ConfigureAwait(false);

        var batch = new List<TRecord>(BatchSize);

        await _observer.StageAsync(Name, PipelineStage.Parse, fetched.Source, cancellationToken)
            .ConfigureAwait(false);

        await foreach (var record in ParseAsync(fetched, cancellationToken).ConfigureAwait(false))
        {
            batch.Add(record);
            parsed++;

            if (batch.Count < BatchSize)
            {
                continue;
            }

            if (!await FlushAsync(session, batch, validation, cancellationToken).ConfigureAwait(false))
            {
                // Validation error: stop reading the file rather than parse the remaining
                // hundred-odd megabytes for a load that is already abandoned.
                return (parsed, 0, validation);
            }

            batch.Clear();
        }

        if (batch.Count > 0 &&
            !await FlushAsync(session, batch, validation, cancellationToken).ConfigureAwait(false))
        {
            return (parsed, 0, validation);
        }

        await _observer.StageAsync(Name, PipelineStage.Load, fetched.Source, cancellationToken)
            .ConfigureAwait(false);

        loaded = await session.CommitAsync(cancellationToken).ConfigureAwait(false);

        Logger.LogDebug(
            "Streamed {Parsed:N0} record(s) from {Size:N0} bytes in {Ms:N0} ms",
            parsed,
            fetched.SizeBytes,
            stopwatch.ElapsedMilliseconds);

        return (parsed, loaded, validation);
    }

    /// <summary>Validate then stage one batch. Returns false when the load must stop.</summary>
    private async Task<bool> FlushAsync(
        IRecordSinkSession<TRecord> session,
        List<TRecord> batch,
        ValidationResult validation,
        CancellationToken cancellationToken)
    {
        await _observer.StageAsync(Name, PipelineStage.Validate, null, cancellationToken)
            .ConfigureAwait(false);

        var batchResult = await ValidateAsync(batch, cancellationToken).ConfigureAwait(false);
        validation.Merge(batchResult);

        if (validation.HasErrors)
        {
            return false;
        }

        await session.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<IngestionRun> FailAsync(
        IngestionRun run,
        string message,
        CancellationToken cancellationToken)
    {
        var failed = run with
        {
            Status = IngestionRunStatus.Failed,
            CompletedAt = DateTimeOffset.UtcNow,
            ErrorMessage = Truncate(message, 2000),
        };

        // A run only has an id once StartAsync has written it; a fetch that failed before
        // that has nothing to update.
        if (failed.Id != 0)
        {
            await _runStore.CompleteAsync(failed, cancellationToken).ConfigureAwait(false);
        }

        return failed;
    }

    private static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
