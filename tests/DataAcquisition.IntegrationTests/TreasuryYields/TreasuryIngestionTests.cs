using System.Runtime.CompilerServices;
using Dapper;
using DataAcquisition.Core.Abstractions;
using DataAcquisition.Core.Model;
using DataAcquisition.Core.Pipeline;
using DataAcquisition.Core.Validation;
using DataAcquisition.IntegrationTests.Infrastructure;
using DataAcquisition.Providers.TreasuryYields.Model;
using DataAcquisition.Providers.TreasuryYields.Parsing;
using DataAcquisition.Providers.TreasuryYields.Storage;
using DataAcquisition.Providers.TreasuryYields.Validation;
using DataAcquisition.Storage.Loading;
using DataAcquisition.Storage.Migrations;
using DataAcquisition.Storage.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataAcquisition.IntegrationTests.TreasuryYields;

/// <summary>
/// The Treasury provider's parsing, validation and loading over a real trimmed feed, reading
/// a fixture instead of calling home.treasury.gov. Only discovery and fetch are stubbed.
/// </summary>
internal sealed class FixtureTreasuryProvider(
    string fixturePath,
    IIngestionRunStore runStore,
    IRecordSink<TreasuryParYield> sink,
    ILogger<FixtureTreasuryProvider> logger)
    : DataProviderBase<TreasuryParYield>(runStore, sink, logger)
{
    private readonly TreasuryYieldValidator _validator = new();

    public override string Name => "TreasuryYields";

    public override string AssetClass => AssetClasses.UsTreasuries;

    protected override int BatchSize => 32;

    public override Task<IReadOnlyList<DataSource>> DiscoverAsync(
        IngestionOptions options,
        CancellationToken cancellationToken)
    {
        _validator.Reset();
        return Task.FromResult<IReadOnlyList<DataSource>>(
        [
            new DataSource(
                "daily_treasury_yield_curve-2026",
                new Uri("https://home.treasury.gov/fixture"),
                new DateOnly(2026, 12, 31),
                "UST",
                "U.S. Treasury par yield curve 2026"),
        ]);
    }

    public override Task<FetchedSource> FetchAsync(DataSource source, CancellationToken cancellationToken)
    {
        var info = new FileInfo(fixturePath);
        using var stream = info.OpenRead();
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
        return Task.FromResult(new FetchedSource(source, fixturePath, hash, info.Length, FromCache: true));
    }

    public override async IAsyncEnumerable<TreasuryParYield> ParseAsync(
        FetchedSource source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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

[Collection(PostgresCollection.Name)]
public sealed class TreasuryIngestionTests(PostgresFixture postgres)
{
    private static string Fixture => Path.Combine(
        AppContext.BaseDirectory, "fixtures", "treasury", "par-yields-2026-sample.xml");

    private async Task<(IngestionRunStore Store, CopyBulkLoader<TreasuryParYield> Sink)> PrepareAsync()
    {
        await postgres.ResetAsync();
        var migration = new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance)
            .Upgrade(postgres.ConnectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());

        return (
            new IngestionRunStore(postgres.DataSource),
            new CopyBulkLoader<TreasuryParYield>(
                postgres.DataSource,
                new TreasuryParYieldTableMap(),
                NullLogger<CopyBulkLoader<TreasuryParYield>>.Instance));
    }

    private static FixtureTreasuryProvider Provider(
        IngestionRunStore store,
        CopyBulkLoader<TreasuryParYield> sink) =>
        new(Fixture, store, sink, NullLogger<FixtureTreasuryProvider>.Instance);

    [Fact]
    public async Task Loads_the_curve_end_to_end()
    {
        var (store, sink) = await PrepareAsync();

        var summary = await Provider(store, sink).IngestAsync(new IngestionOptions(), CancellationToken.None);

        Assert.True(summary.IsSuccess);
        var run = Assert.Single(summary.Runs);

        // Five business days x fourteen tenors, the BC_30YEARDISPLAY duplicate excluded.
        Assert.Equal(70, run.RecordsLoaded);
        Assert.Equal(AssetClasses.UsTreasuries, run.AssetClass);

        await using var connection = await postgres.DataSource.OpenConnectionAsync();

        Assert.Equal(70, await connection.ExecuteScalarAsync<long>(
            "select count(*) from treasury_par_yields"));

        // Treasury publishes percentages: 4.14 means 4.14%.
        Assert.Equal(4.1400m, await connection.ExecuteScalarAsync<decimal>(
            "select rate_percent from treasury_par_yields where quote_date = date '2026-06-30' and tenor_months = 24"));

        // One row at 360 months, not two.
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(
            "select count(*) from treasury_par_yields where quote_date = date '2026-08-31' and tenor_months = 360"));

        // The 1.5-month point survives a numeric tenor column: one per business day, which
        // is why the tenor column is numeric(6,1) rather than an integer.
        Assert.Equal(5, await connection.ExecuteScalarAsync<long>(
            "select count(*) from treasury_par_yields where tenor_months = 1.5"));
    }

    [Fact]
    public async Task Re_ingesting_the_same_feed_is_skipped()
    {
        var (store, sink) = await PrepareAsync();

        Assert.Equal(1, (await Provider(store, sink)
            .IngestAsync(new IngestionOptions(), CancellationToken.None)).Succeeded);

        var second = await Provider(store, sink)
            .IngestAsync(new IngestionOptions(), CancellationToken.None);

        Assert.Equal(1, second.Skipped);

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        Assert.Equal(70, await connection.ExecuteScalarAsync<long>(
            "select count(*) from treasury_par_yields"));
    }

    [Fact]
    public async Task Forcing_a_re_ingest_upserts_rather_than_duplicating()
    {
        var (store, sink) = await PrepareAsync();

        await Provider(store, sink).IngestAsync(new IngestionOptions(), CancellationToken.None);
        var forced = await Provider(store, sink)
            .IngestAsync(new IngestionOptions { Force = true }, CancellationToken.None);

        Assert.Equal(1, forced.Succeeded);

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        Assert.Equal(70, await connection.ExecuteScalarAsync<long>(
            "select count(*) from treasury_par_yields"));
    }

    [Fact]
    public async Task Both_asset_classes_share_one_run_table()
    {
        // The audit trail is provider-agnostic: a second asset class needed no new tracking.
        var (store, sink) = await PrepareAsync();
        await Provider(store, sink).IngestAsync(new IngestionOptions(), CancellationToken.None);

        var runs = await store.GetRunsAsync("TreasuryYields", 10, CancellationToken.None);
        var run = Assert.Single(runs);

        Assert.Equal(AssetClasses.UsTreasuries, run.AssetClass);
        Assert.Equal("UST", run.SubjectId);
    }

    [Fact]
    public async Task The_rate_check_constraint_rejects_an_implausible_yield()
    {
        await PrepareAsync();

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            connection.ExecuteAsync("""
                insert into treasury_par_yields (quote_date, tenor_months, tenor_label, rate_percent)
                values (date '2026-08-31', 24, '2Y', 99)
                """));

        Assert.Contains("treasury_par_yields_rate_plausible", ex.Message, StringComparison.Ordinal);
    }
}
