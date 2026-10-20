using DataAcquisition.Core.Model;
using DataAcquisition.Core.Pipeline;
using DataAcquisition.UnitTests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataAcquisition.UnitTests.Core;

public sealed class DataProviderBaseTests
{
    private static DataSource Source(string id = "src-1") =>
        new(id, new Uri($"https://example.test/{id}"), new DateOnly(2026, 8, 31), "CIK-1", $"Fake {id}");

    private static FakeWidgetProvider Build(
        out InMemoryRunStore runStore,
        out InMemoryRecordSink<WidgetRecord> sink,
        out RecordingPipelineObserver observer,
        IReadOnlyList<WidgetRecord>? records = null,
        string? cachedHash = null,
        string sha256 = "hash-aaa")
    {
        runStore = new InMemoryRunStore();
        sink = new InMemoryRecordSink<WidgetRecord>();
        observer = new RecordingPipelineObserver();

        return new FakeWidgetProvider(
            runStore,
            sink,
            NullLogger<FakeWidgetProvider>.Instance,
            [Source()],
            records ?? [new WidgetRecord("a", 1m), new WidgetRecord("b", 2m), new WidgetRecord("c", 3m)],
            sha256,
            cachedHash,
            observer);
    }

    [Fact]
    public async Task Pipeline_runs_stages_in_template_order()
    {
        var provider = Build(out _, out _, out var observer);

        await provider.IngestAsync(new IngestionOptions(), CancellationToken.None);

        // Three records with BatchSize 2 => validate/write twice, so collapse repeats.
        Assert.Equal(
            [
                PipelineStage.Discover,
                PipelineStage.Fetch,
                PipelineStage.Parse,
                PipelineStage.Validate,
                PipelineStage.Load,
                PipelineStage.Record,
            ],
            observer.Distinct());
    }

    [Fact]
    public async Task Already_ingested_source_is_skipped_without_fetching()
    {
        var provider = Build(out var runStore, out var sink, out var observer, cachedHash: "hash-aaa");
        runStore.AlreadyIngested.Add("FakeWidgets|src-1|hash-aaa");

        var summary = await provider.IngestAsync(new IngestionOptions(), CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, summary.Succeeded);
        Assert.Equal(IngestionRunStatus.Skipped, summary.Runs[0].Status);

        // The cached hash answered the question, so nothing was fetched or loaded.
        Assert.Equal(0, provider.FetchCount);
        Assert.Equal(0, provider.ParseCount);
        Assert.Empty(sink.Committed);
        Assert.Contains(PipelineStage.Skip, observer.Stages);
        Assert.DoesNotContain(PipelineStage.Fetch, observer.Stages);

        // A skipped source writes no new run row.
        Assert.Empty(runStore.Started);
    }

    [Fact]
    public async Task Already_ingested_is_detected_after_fetch_when_hash_is_not_known_up_front()
    {
        // No cached hash: the content hash only exists once the bytes are here.
        var provider = Build(out var runStore, out var sink, out _, sha256: "hash-bbb");
        runStore.AlreadyIngested.Add("FakeWidgets|src-1|hash-bbb");

        var summary = await provider.IngestAsync(new IngestionOptions(), CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(1, provider.FetchCount);
        Assert.Equal(0, provider.ParseCount);
        Assert.Empty(sink.Committed);
    }

    [Fact]
    public async Task Force_re_ingests_a_source_that_was_already_ingested()
    {
        var provider = Build(out var runStore, out var sink, out _, cachedHash: "hash-aaa");
        runStore.AlreadyIngested.Add("FakeWidgets|src-1|hash-aaa");

        var summary = await provider.IngestAsync(
            new IngestionOptions { Force = true },
            CancellationToken.None);

        Assert.Equal(1, summary.Succeeded);
        Assert.Equal(3, sink.Committed.Count);
    }

    [Fact]
    public async Task Validation_failure_stops_the_load_and_marks_the_run_failed()
    {
        var provider = Build(
            out var runStore,
            out var sink,
            out var observer,
            records:
            [
                new WidgetRecord("a", 1m),
                new WidgetRecord("bad", -5m),   // breaks value-range
                new WidgetRecord("c", 3m),
            ]);

        var summary = await provider.IngestAsync(new IngestionOptions(), CancellationToken.None);

        Assert.Equal(1, summary.Failed);
        Assert.False(summary.IsSuccess);

        var run = summary.Runs[0];
        Assert.Equal(IngestionRunStatus.Failed, run.Status);
        Assert.Equal(1, run.ValidationErrors);
        Assert.Equal(0, run.RecordsLoaded);
        Assert.Contains("validation failed", run.ErrorMessage, StringComparison.Ordinal);

        // Nothing committed, and the session rolled back.
        Assert.Empty(sink.Committed);
        Assert.Equal(0, sink.CommitCount);
        Assert.Equal(1, sink.RollbackCount);

        // The load stage was never reached.
        Assert.DoesNotContain(PipelineStage.Load, observer.Stages);

        // The failure is recorded against the run row that was opened.
        Assert.Single(runStore.Started);
        Assert.Equal(IngestionRunStatus.Failed, Assert.Single(runStore.Completed).Status);
    }

    [Fact]
    public async Task Validation_failure_stops_reading_the_rest_of_the_file()
    {
        // Batch size 2: the first batch fails, so the third record is never staged.
        var provider = Build(
            out _,
            out var sink,
            out _,
            records:
            [
                new WidgetRecord("a", -1m),
                new WidgetRecord("b", 2m),
                new WidgetRecord("c", 3m),
            ]);

        await provider.IngestAsync(new IngestionOptions(), CancellationToken.None);

        Assert.Empty(sink.Staged);
        Assert.Empty(sink.Committed);
    }

    [Fact]
    public async Task Successful_run_records_counts_and_commits_once()
    {
        var provider = Build(out var runStore, out var sink, out _);

        var summary = await provider.IngestAsync(new IngestionOptions(), CancellationToken.None);

        Assert.True(summary.IsSuccess);
        Assert.Equal(1, summary.Succeeded);

        var run = summary.Runs[0];
        Assert.Equal(3, run.RecordsParsed);
        Assert.Equal(3, run.RecordsLoaded);
        Assert.Equal(0, run.ValidationErrors);
        Assert.Equal("hash-aaa", run.Sha256);
        Assert.NotNull(run.CompletedAt);

        Assert.Equal(1, sink.CommitCount);
        Assert.Equal(0, sink.RollbackCount);
        Assert.Equal(3, sink.Committed.Count);
        Assert.Single(runStore.Started);
    }

    [Fact]
    public async Task Dry_run_discovers_without_fetching_or_loading()
    {
        var provider = Build(out var runStore, out var sink, out _);

        var summary = await provider.IngestAsync(
            new IngestionOptions { DryRun = true },
            CancellationToken.None);

        Assert.Equal(1, summary.Skipped);
        Assert.Equal(0, provider.FetchCount);
        Assert.Empty(sink.Committed);
        Assert.Empty(runStore.Started);
    }
}
