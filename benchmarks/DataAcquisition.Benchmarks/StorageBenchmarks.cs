using BenchmarkDotNet.Attributes;
using Dapper;
using DataAcquisition.Core.Model;
using DataAcquisition.Providers.SecAbsEe.Model;
using DataAcquisition.Providers.SecAbsEe.Parsing;
using DataAcquisition.Providers.SecAbsEe.Storage;
using DataAcquisition.Storage.Loading;
using DataAcquisition.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace DataAcquisition.Benchmarks;

/// <summary>
/// Bulk-load throughput against a real PostgreSQL, and the time the report's metric queries
/// take over a loaded pool. Needs a database: the CI job supplies one as a service
/// container, and locally <c>docker compose up</c> does.
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class StorageBenchmarks
{
    /// <summary>Records per load, chosen to sit in the same order as a real monthly exhibit.</summary>
    [Params(10_000)]
    public int Records { get; set; }

    private NpgsqlDataSource _dataSource = null!;
    private List<AutoLoanRecord> _records = [];
    private CopyBulkLoader<AutoLoanRecord> _copyLoader = null!;
    private DataSource _source = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable("FIDA_CONNECTION_STRING")
            ?? "Host=localhost;Port=55432;Database=fida_bench;Username=fida;Password=fida_local_dev";

        new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance).Upgrade(connectionString);

        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();

        var map = new AutoLoanTableMap();
        _copyLoader = new CopyBulkLoader<AutoLoanRecord>(
            _dataSource, map, NullLogger<CopyBulkLoader<AutoLoanRecord>>.Instance);

        // Real row shapes from the fixture, re-keyed up to the benchmark's record count.
        var template = new List<AutoLoanRecord>();
        await foreach (var record in AutoLoanXmlParser.ParseFileAsync(
            Fixtures.Path("sec-abs-ee", "gmcar-2026-08-200.xml"), "BENCH"))
        {
            template.Add(record);
        }

        _records = [.. Enumerable.Range(0, Records).Select(i =>
            template[i % template.Count] with { AssetNumber = $"bench-{i:D7}" })];

        _source = new DataSource(
            "bench", new Uri("https://example.test/bench"), new DateOnly(2026, 8, 31), "BENCH", "Benchmark");

        // Seed once so the query benchmarks have a pool to read.
        await using var session = await _copyLoader.BeginAsync(_source, CancellationToken.None);
        await session.WriteAsync(_records, CancellationToken.None);
        await session.CommitAsync(CancellationToken.None);

        await using var connection = await _dataSource.OpenConnectionAsync();
        await connection.ExecuteAsync("analyze auto_loan_asset_data");
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await using (var connection = await _dataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("delete from auto_loan_asset_data where trust_cik = 'BENCH'");
        }

        await _dataSource.DisposeAsync();
    }

    [Benchmark(Description = "Binary COPY into staging then upsert")]
    public async Task<long> CopyLoad()
    {
        await using var session = await _copyLoader.BeginAsync(_source, CancellationToken.None);
        await session.WriteAsync(_records, CancellationToken.None);
        return await session.CommitAsync(CancellationToken.None);
    }

    [Benchmark(Description = "Pool metric query (v_pool_period)")]
    public async Task<int> PoolPeriodQuery()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync("select * from v_pool_period");
        return rows.AsList().Count;
    }

    [Benchmark(Description = "Spread query (v_pool_spread)")]
    public async Task<int> SpreadQuery()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync("select * from v_pool_spread");
        return rows.AsList().Count;
    }

    [Benchmark(Description = "Cumulative loss query (v_pool_cumulative_loss)")]
    public async Task<int> CumulativeLossQuery()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        var rows = await connection.QueryAsync("select * from v_pool_cumulative_loss");
        return rows.AsList().Count;
    }
}
