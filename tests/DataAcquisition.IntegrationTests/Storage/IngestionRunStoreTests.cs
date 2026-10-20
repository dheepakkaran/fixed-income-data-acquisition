using DataAcquisition.Core.Model;
using DataAcquisition.IntegrationTests.Infrastructure;
using DataAcquisition.Storage.Migrations;
using DataAcquisition.Storage.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataAcquisition.IntegrationTests.Storage;

[Collection(PostgresCollection.Name)]
public sealed class IngestionRunStoreTests(PostgresFixture postgres)
{
    private async Task<IngestionRunStore> StoreAsync()
    {
        await postgres.ResetAsync();
        var result = new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance)
            .Upgrade(postgres.ConnectionString);
        Assert.True(result.Successful, result.Error?.ToString());
        return new IngestionRunStore(postgres.DataSource);
    }

    private static IngestionRun NewRun(string sourceId = "acc-1", string? sha = "hash-aaa") => new()
    {
        ProviderName = "SecAbsEe",
        AssetClass = AssetClasses.AutoLoanAbs,
        SourceId = sourceId,
        Sha256 = sha,
        SubjectId = "2099048",
        ReportingPeriod = new DateOnly(2026, 8, 31),
        StartedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Start_assigns_an_id_and_persists_the_run()
    {
        var store = await StoreAsync();

        var started = await store.StartAsync(NewRun(), CancellationToken.None);

        Assert.True(started.Id > 0);

        var runs = await store.GetRunsAsync("SecAbsEe", 10, CancellationToken.None);
        var stored = Assert.Single(runs);
        Assert.Equal(started.Id, stored.Id);
        Assert.Equal(IngestionRunStatus.Running, stored.Status);
        Assert.Equal(AssetClasses.AutoLoanAbs, stored.AssetClass);
        Assert.Equal(new DateOnly(2026, 8, 31), stored.ReportingPeriod);
    }

    [Fact]
    public async Task Complete_writes_the_terminal_state_and_counts()
    {
        var store = await StoreAsync();
        var started = await store.StartAsync(NewRun(), CancellationToken.None);

        await store.CompleteAsync(
            started with
            {
                Status = IngestionRunStatus.Succeeded,
                CompletedAt = started.StartedAt.AddSeconds(42),
                RecordsParsed = 29_152,
                RecordsLoaded = 29_152,
                ValidationWarnings = 546,
            },
            CancellationToken.None);

        var stored = Assert.Single(await store.GetRunsAsync(null, 10, CancellationToken.None));
        Assert.Equal(IngestionRunStatus.Succeeded, stored.Status);
        Assert.Equal(29_152, stored.RecordsParsed);
        Assert.Equal(29_152, stored.RecordsLoaded);
        Assert.Equal(546, stored.ValidationWarnings);
        Assert.NotNull(stored.Duration);
    }

    [Fact]
    public async Task Already_ingested_is_true_only_for_a_succeeded_run_with_the_same_hash()
    {
        var store = await StoreAsync();
        var started = await store.StartAsync(NewRun(sha: "hash-aaa"), CancellationToken.None);

        // Still Running: not yet ingested.
        Assert.False(await store.IsAlreadyIngestedAsync(
            "SecAbsEe", "acc-1", "hash-aaa", CancellationToken.None));

        await store.CompleteAsync(
            started with { Status = IngestionRunStatus.Succeeded, CompletedAt = DateTimeOffset.UtcNow },
            CancellationToken.None);

        Assert.True(await store.IsAlreadyIngestedAsync(
            "SecAbsEe", "acc-1", "hash-aaa", CancellationToken.None));

        // A corrected re-filing under the same accession number has different content,
        // so it is new work.
        Assert.False(await store.IsAlreadyIngestedAsync(
            "SecAbsEe", "acc-1", "hash-bbb", CancellationToken.None));

        // Another provider's identically named source is unrelated.
        Assert.False(await store.IsAlreadyIngestedAsync(
            "TreasuryYields", "acc-1", "hash-aaa", CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_run_does_not_count_as_ingested()
    {
        var store = await StoreAsync();
        var started = await store.StartAsync(NewRun(), CancellationToken.None);

        await store.CompleteAsync(
            started with
            {
                Status = IngestionRunStatus.Failed,
                CompletedAt = DateTimeOffset.UtcNow,
                ErrorMessage = "validation failed: 3 error(s)",
            },
            CancellationToken.None);

        Assert.False(await store.IsAlreadyIngestedAsync(
            "SecAbsEe", "acc-1", "hash-aaa", CancellationToken.None));

        var stored = Assert.Single(await store.GetRunsAsync(null, 10, CancellationToken.None));
        Assert.Equal(IngestionRunStatus.Failed, stored.Status);
        Assert.Contains("validation failed", stored.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recording_the_same_raw_file_twice_keeps_one_row()
    {
        var store = await StoreAsync();
        var source = new DataSource(
            "acc-1", new Uri("https://www.sec.gov/Archives/x.xml"), new DateOnly(2026, 8, 31), "2099048", "GMCAR");

        await store.RecordSourceFileAsync("SecAbsEe", source, "hash-aaa", 122_184_076, CancellationToken.None);
        await store.RecordSourceFileAsync("SecAbsEe", source, "hash-aaa", 122_184_076, CancellationToken.None);

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from source_files";
        var count = (long)(await command.ExecuteScalarAsync())!;

        Assert.Equal(1, count);
    }
}
