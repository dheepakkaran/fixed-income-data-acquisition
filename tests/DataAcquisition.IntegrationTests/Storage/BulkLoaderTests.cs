using Dapper;
using DataAcquisition.Core.Model;
using DataAcquisition.IntegrationTests.Fakes;
using DataAcquisition.IntegrationTests.Infrastructure;
using DataAcquisition.Storage.Loading;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataAcquisition.IntegrationTests.Storage;

[Collection(PostgresCollection.Name)]
public sealed class BulkLoaderTests(PostgresFixture postgres)
{
    private static readonly DateOnly Period = new(2026, 8, 31);

    private static DataSource Source() =>
        new("src-1", new Uri("https://example.test/src-1"), Period, "CIK-1", "Widgets");

    private CopyBulkLoader<WidgetRow> CopyLoader() =>
        new(postgres.DataSource, new WidgetTableMap(), NullLogger<CopyBulkLoader<WidgetRow>>.Instance);

    private InsertBulkLoader<WidgetRow> InsertLoader() =>
        new(postgres.DataSource, new WidgetTableMap(), NullLogger<InsertBulkLoader<WidgetRow>>.Instance);

    private async Task PrepareTableAsync()
    {
        await postgres.ResetAsync();
        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        await connection.ExecuteAsync(WidgetTableMap.CreateTableSql);
    }

    private async Task<long> CountAsync()
    {
        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<long>("select count(*) from widgets");
    }

    private static List<WidgetRow> Rows(int count, decimal balance = 1000m) =>
        [.. Enumerable.Range(1, count).Select(i =>
            new WidgetRow("TRUST-A", Period, $"asset-{i:D5}", balance + i, 600 + i))];

    [Fact]
    public async Task Copy_loads_every_row()
    {
        await PrepareTableAsync();

        await using (var session = await CopyLoader().BeginAsync(Source(), CancellationToken.None))
        {
            await session.WriteAsync(Rows(5_000), CancellationToken.None);
            var affected = await session.CommitAsync(CancellationToken.None);
            Assert.Equal(5_000, affected);
        }

        Assert.Equal(5_000, await CountAsync());
    }

    [Fact]
    public async Task Re_running_a_load_does_not_duplicate_rows()
    {
        await PrepareTableAsync();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var session = await CopyLoader().BeginAsync(Source(), CancellationToken.None);
            await session.WriteAsync(Rows(500), CancellationToken.None);
            await session.CommitAsync(CancellationToken.None);
        }

        // Same natural keys three times over: upserted, not appended.
        Assert.Equal(500, await CountAsync());
    }

    [Fact]
    public async Task Re_running_with_corrected_values_updates_in_place()
    {
        await PrepareTableAsync();

        await using (var first = await CopyLoader().BeginAsync(Source(), CancellationToken.None))
        {
            await first.WriteAsync(Rows(10, balance: 1000m), CancellationToken.None);
            await first.CommitAsync(CancellationToken.None);
        }

        await using (var second = await CopyLoader().BeginAsync(Source(), CancellationToken.None))
        {
            await second.WriteAsync(Rows(10, balance: 2000m), CancellationToken.None);
            await second.CommitAsync(CancellationToken.None);
        }

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        var balance = await connection.ExecuteScalarAsync<decimal>(
            "select balance from widgets where asset_id = 'asset-00001'");

        Assert.Equal(10, await CountAsync());
        Assert.Equal(2001m, balance);
    }

    [Fact]
    public async Task A_failed_load_rolls_back_completely()
    {
        await PrepareTableAsync();

        // Seed a committed row so the test distinguishes "rolled back" from "empty table".
        await using (var seed = await CopyLoader().BeginAsync(Source(), CancellationToken.None))
        {
            await seed.WriteAsync([new WidgetRow("TRUST-A", Period, "seed", 1m, 700)], CancellationToken.None);
            await seed.CommitAsync(CancellationToken.None);
        }

        // Stage a large batch, then dispose without committing, exactly as the pipeline does
        // when validation fails partway through a file.
        await using (var abandoned = await CopyLoader().BeginAsync(Source(), CancellationToken.None))
        {
            await abandoned.WriteAsync(Rows(2_000), CancellationToken.None);
        }

        Assert.Equal(1, await CountAsync());

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        var seeded = await connection.ExecuteScalarAsync<long>(
            "select count(*) from widgets where asset_id = 'seed'");
        Assert.Equal(1, seeded);

        // The staging table was temporary and went with the transaction.
        var staging = await connection.ExecuteScalarAsync<bool>(
            "select exists (select 1 from information_schema.tables where table_name = 'staging_widgets')");
        Assert.False(staging);
    }

    [Fact]
    public async Task An_exception_mid_batch_leaves_the_target_untouched()
    {
        await PrepareTableAsync();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var session = await CopyLoader().BeginAsync(Source(), CancellationToken.None);
            await session.WriteAsync(Rows(100), CancellationToken.None);

            // A null in a not-null key column: the server rejects the merge on commit.
            await session.WriteAsync([new WidgetRow(null!, Period, "bad", 1m, 1)], CancellationToken.None);
            await session.CommitAsync(CancellationToken.None);
        });

        Assert.Equal(0, await CountAsync());
    }

    [Fact]
    public async Task Both_strategies_produce_the_same_rows()
    {
        // The benchmark compares COPY against row-by-row INSERT; that comparison is only
        // meaningful if both write identical data.
        await PrepareTableAsync();
        await using (var copy = await CopyLoader().BeginAsync(Source(), CancellationToken.None))
        {
            await copy.WriteAsync(Rows(200), CancellationToken.None);
            await copy.CommitAsync(CancellationToken.None);
        }

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        var viaCopy = (await connection.QueryAsync<(string AssetId, decimal? Balance, int? Score)>(
            "select asset_id, balance, score from widgets order by asset_id")).ToList();

        await PrepareTableAsync();
        await using (var insert = await InsertLoader().BeginAsync(Source(), CancellationToken.None))
        {
            await insert.WriteAsync(Rows(200), CancellationToken.None);
            await insert.CommitAsync(CancellationToken.None);
        }

        var viaInsert = (await connection.QueryAsync<(string AssetId, decimal? Balance, int? Score)>(
            "select asset_id, balance, score from widgets order by asset_id")).ToList();

        Assert.Equal(200, viaCopy.Count);
        Assert.Equal(viaCopy, viaInsert);
        Assert.Equal("binary-copy", CopyLoader().Strategy);
        Assert.Equal("row-insert", InsertLoader().Strategy);
    }

    [Fact]
    public async Task Nulls_round_trip_through_binary_copy()
    {
        await PrepareTableAsync();

        await using (var session = await CopyLoader().BeginAsync(Source(), CancellationToken.None))
        {
            await session.WriteAsync(
                [new WidgetRow("TRUST-A", Period, "nulls", null, null)],
                CancellationToken.None);
            await session.CommitAsync(CancellationToken.None);
        }

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync<(decimal? Balance, int? Score)>(
            "select balance, score from widgets where asset_id = 'nulls'");

        Assert.Null(row.Balance);
        Assert.Null(row.Score);
    }
}
