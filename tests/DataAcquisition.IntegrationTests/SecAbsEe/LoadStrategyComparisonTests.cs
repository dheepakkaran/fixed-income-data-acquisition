using System.Diagnostics;
using Dapper;
using DataAcquisition.Core.Model;
using DataAcquisition.IntegrationTests.Infrastructure;
using DataAcquisition.Providers.SecAbsEe.Model;
using DataAcquisition.Providers.SecAbsEe.Parsing;
using DataAcquisition.Providers.SecAbsEe.Storage;
using DataAcquisition.Storage.Loading;
using DataAcquisition.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace DataAcquisition.IntegrationTests.SecAbsEe;

/// <summary>
/// Binary COPY against row-by-row INSERT on the same data. This is the measurement that
/// justifies COPY being in the approved stack at all, and the assertion is the standing
/// guard: if the COPY path ever stops being the faster one, the complexity is no longer
/// paying for itself.
/// <para>
/// Row shapes are real, parsed from a trimmed GM Financial exhibit. The volume is brought
/// up to the real exhibit's record count by re-keying those rows, so the comparison runs
/// anywhere, CI included, without a 122 MB file in the repository.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LoadStrategyComparisonTests(PostgresFixture postgres, ITestOutputHelper output)
{
    /// <summary>Records in the real GM Financial 2026-08 exhibit.</summary>
    private const int RealExhibitRecordCount = 29_152;

    private static readonly DateOnly Period = new(2026, 8, 31);

    private static DataSource Source() =>
        new("compare", new Uri("https://example.test/compare"), Period, "2099048", "Comparison");

    private async Task<List<AutoLoanRecord>> BuildAsync(int count)
    {
        var template = new List<AutoLoanRecord>();
        await foreach (var record in AutoLoanXmlParser.ParseFileAsync(Fixtures.Gmcar, "2099048"))
        {
            template.Add(record);
        }

        var records = new List<AutoLoanRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var source = template[i % template.Count];
            records.Add(source with { AssetNumber = $"{source.AssetNumber}-{i:D6}" });
        }

        return records;
    }

    private async Task PrepareAsync()
    {
        await postgres.ResetAsync();
        var migration = new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance)
            .Upgrade(postgres.ConnectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());
    }

    private async Task<TimeSpan> TimeAsync(IBulkLoader<AutoLoanRecord> loader, List<AutoLoanRecord> records)
    {
        var stopwatch = Stopwatch.StartNew();

        await using (var session = await loader.BeginAsync(Source(), CancellationToken.None))
        {
            // Same batching the pipeline uses, so both strategies see identical call shapes.
            for (var offset = 0; offset < records.Count; offset += 10_000)
            {
                var batch = records.GetRange(offset, Math.Min(10_000, records.Count - offset));
                await session.WriteAsync(batch, CancellationToken.None);
            }

            await session.CommitAsync(CancellationToken.None);
        }

        stopwatch.Stop();

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        Assert.Equal(
            records.Count,
            await connection.ExecuteScalarAsync<long>("select count(*) from auto_loan_asset_data"));

        return stopwatch.Elapsed;
    }

    [Fact]
    public async Task Binary_copy_beats_row_by_row_insert_on_the_same_data()
    {
        var records = await BuildAsync(RealExhibitRecordCount);
        var map = new AutoLoanTableMap();

        await PrepareAsync();
        var copy = await TimeAsync(
            new CopyBulkLoader<AutoLoanRecord>(
                postgres.DataSource, map, NullLogger<CopyBulkLoader<AutoLoanRecord>>.Instance),
            records);

        await PrepareAsync();
        var insert = await TimeAsync(
            new InsertBulkLoader<AutoLoanRecord>(
                postgres.DataSource, map, NullLogger<InsertBulkLoader<AutoLoanRecord>>.Instance),
            records);

        var speedup = insert.TotalMilliseconds / copy.TotalMilliseconds;

        output.WriteLine($"records                : {records.Count:N0}");
        output.WriteLine($"binary COPY + upsert   : {copy.TotalMilliseconds:N0} ms "
                       + $"({records.Count / copy.TotalSeconds:N0} rows/s)");
        output.WriteLine($"row-by-row INSERT      : {insert.TotalMilliseconds:N0} ms "
                       + $"({records.Count / insert.TotalSeconds:N0} rows/s)");
        output.WriteLine($"speedup                : {speedup:N1}x");

        Assert.True(
            speedup > 1.0,
            $"COPY took {copy.TotalMilliseconds:N0} ms and INSERT {insert.TotalMilliseconds:N0} ms; "
            + "COPY is supposed to be the faster path.");
    }
}
