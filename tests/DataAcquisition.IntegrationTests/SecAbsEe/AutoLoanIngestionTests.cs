using Dapper;
using DataAcquisition.Core.Model;
using DataAcquisition.IntegrationTests.Infrastructure;
using DataAcquisition.Providers.SecAbsEe.Model;
using DataAcquisition.Providers.SecAbsEe.Storage;
using DataAcquisition.Storage.Loading;
using DataAcquisition.Storage.Migrations;
using DataAcquisition.Storage.Repositories;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataAcquisition.IntegrationTests.SecAbsEe;

/// <summary>
/// The whole pipeline over a real trimmed exhibit, into a real PostgreSQL: parse, validate,
/// COPY into staging, upsert into <c>auto_loan_asset_data</c>, record the run.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AutoLoanIngestionTests(PostgresFixture postgres)
{
    private static readonly DateOnly Period = new(2026, 8, 31);

    private async Task<(IngestionRunStore Store, CopyBulkLoader<AutoLoanRecord> Sink)> PrepareAsync()
    {
        await postgres.ResetAsync();
        var migration = new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance)
            .Upgrade(postgres.ConnectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());

        return (
            new IngestionRunStore(postgres.DataSource),
            new CopyBulkLoader<AutoLoanRecord>(
                postgres.DataSource,
                new AutoLoanTableMap(),
                NullLogger<CopyBulkLoader<AutoLoanRecord>>.Instance));
    }

    private FixtureProvider Provider(
        IngestionRunStore store,
        CopyBulkLoader<AutoLoanRecord> sink,
        string fixture,
        string cik) =>
        new(fixture, cik, Period, store, sink, NullLogger<FixtureProvider>.Instance);

    [Fact]
    public async Task Loads_a_real_exhibit_end_to_end()
    {
        var (store, sink) = await PrepareAsync();
        var provider = Provider(store, sink, Fixtures.Gmcar, "2099048");

        var summary = await provider.IngestAsync(new IngestionOptions(), CancellationToken.None);

        Assert.True(summary.IsSuccess);
        var run = Assert.Single(summary.Runs);
        Assert.Equal(IngestionRunStatus.Succeeded, run.Status);
        Assert.Equal(200, run.RecordsParsed);
        Assert.Equal(200, run.RecordsLoaded);
        Assert.Equal(0, run.ValidationErrors);

        await using var connection = await postgres.DataSource.OpenConnectionAsync();

        // Values asserted against the filing itself.
        var row = await connection.QuerySingleAsync<(decimal? Balance, int? Score, int? Dq, decimal? Rate)>(
            """
            select ending_balance, obligor_credit_score, current_delinquency_days, original_interest_rate
            from auto_loan_asset_data
            where trust_cik = '2099048' and asset_number = '0002099048 - 000002'
            """);

        Assert.Equal(3_527.33m, row.Balance);
        Assert.Equal(867, row.Score);
        Assert.Equal(0, row.Dq);
        Assert.Equal(0m, row.Rate);

        var total = await connection.ExecuteScalarAsync<decimal>(
            "select sum(ending_balance) from auto_loan_asset_data where trust_cik = '2099048'");
        Assert.Equal(3_939_464.74m, total);

        // The optional elements really are null in the database, not zero.
        Assert.Equal(4, await connection.ExecuteScalarAsync<long>(
            "select count(*) from auto_loan_asset_data where current_delinquency_days is null"));
        Assert.Equal(3, await connection.ExecuteScalarAsync<long>(
            "select count(*) from auto_loan_asset_data where obligor_credit_score is null"));
    }

    [Fact]
    public async Task Loads_the_single_line_exhibit_end_to_end()
    {
        var (store, sink) = await PrepareAsync();
        var provider = Provider(store, sink, Fixtures.Exeter, "2013062");

        var summary = await provider.IngestAsync(new IngestionOptions(), CancellationToken.None);

        Assert.True(summary.IsSuccess);
        Assert.Equal(200, summary.Runs[0].RecordsLoaded);

        await using var connection = await postgres.DataSource.OpenConnectionAsync();

        Assert.Equal(167_275.92m, await connection.ExecuteScalarAsync<decimal>(
            "select sum(ending_balance) from auto_loan_asset_data"));

        // Only ten of these two hundred loans are still open.
        Assert.Equal(10, await connection.ExecuteScalarAsync<long>(
            "select count(*) from auto_loan_asset_data where ending_balance > 0"));

        // Zero credit scores were read as absent, so no row carries a score of zero.
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
            "select count(*) from auto_loan_asset_data where obligor_credit_score = 0"));
        Assert.Equal(9, await connection.ExecuteScalarAsync<long>(
            "select count(*) from auto_loan_asset_data where obligor_credit_score is null"));
    }

    [Fact]
    public async Task Re_ingesting_the_same_exhibit_is_skipped()
    {
        var (store, sink) = await PrepareAsync();

        var first = await Provider(store, sink, Fixtures.Gmcar, "2099048")
            .IngestAsync(new IngestionOptions(), CancellationToken.None);
        Assert.Equal(1, first.Succeeded);

        // A fresh provider instance, as a second CLI invocation would be.
        var second = await Provider(store, sink, Fixtures.Gmcar, "2099048")
            .IngestAsync(new IngestionOptions(), CancellationToken.None);

        Assert.Equal(1, second.Skipped);
        Assert.Equal(0, second.Succeeded);

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        Assert.Equal(200, await connection.ExecuteScalarAsync<long>(
            "select count(*) from auto_loan_asset_data"));
    }

    [Fact]
    public async Task Forcing_a_re_ingest_leaves_the_row_count_unchanged()
    {
        var (store, sink) = await PrepareAsync();

        await Provider(store, sink, Fixtures.Gmcar, "2099048")
            .IngestAsync(new IngestionOptions(), CancellationToken.None);

        var forced = await Provider(store, sink, Fixtures.Gmcar, "2099048")
            .IngestAsync(new IngestionOptions { Force = true }, CancellationToken.None);

        Assert.Equal(1, forced.Succeeded);

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        Assert.Equal(200, await connection.ExecuteScalarAsync<long>(
            "select count(*) from auto_loan_asset_data"));

        // Two succeeded runs for the same content hash: --force is supported, which is why
        // the identity index is not unique.
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>(
            "select count(*) from ingestion_runs where status = 1"));
    }

    [Fact]
    public async Task Two_trusts_in_the_same_period_coexist()
    {
        var (store, sink) = await PrepareAsync();

        await Provider(store, sink, Fixtures.Gmcar, "2099048")
            .IngestAsync(new IngestionOptions(), CancellationToken.None);
        await Provider(store, sink, Fixtures.Exeter, "2013062")
            .IngestAsync(new IngestionOptions(), CancellationToken.None);

        await using var connection = await postgres.DataSource.OpenConnectionAsync();

        Assert.Equal(400, await connection.ExecuteScalarAsync<long>(
            "select count(*) from auto_loan_asset_data"));
        Assert.Equal(2, await connection.ExecuteScalarAsync<long>(
            "select count(distinct trust_cik) from auto_loan_asset_data"));
    }

    [Fact]
    public async Task The_check_constraint_rejects_an_unenumerated_zero_balance_code()
    {
        // Validation catches this first; the constraint is the backstop if it ever does not.
        await PrepareAsync();

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            connection.ExecuteAsync("""
                insert into auto_loan_asset_data
                    (trust_cik, reporting_period_end, asset_number, zero_balance_code)
                values ('X', date '2026-08-31', 'a', 7)
                """));

        Assert.Contains("auto_loan_zero_balance_code_known", ex.Message, StringComparison.Ordinal);
    }
}
