using Dapper;
using DataAcquisition.IntegrationTests.Infrastructure;
using DataAcquisition.Reports;
using DataAcquisition.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataAcquisition.IntegrationTests.Reports;

/// <summary>
/// The report end to end: database to files on disk, through the same repository and
/// generator the CLI uses. The metric SQL is covered separately; this covers the code that
/// reads it, which a view test cannot reach.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReportGeneratorTests(PostgresFixture postgres) : IDisposable
{
    private readonly string _output = Path.Combine(
        Path.GetTempPath(), "fida-report-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_output))
        {
            Directory.Delete(_output, recursive: true);
        }
    }

    private static readonly TrustDescriptor[] Trusts =
    [
        new("2099048", "GM Financial Consumer Automobile Receivables Trust 2026-1", "Prime"),
        new("2013062", "Exeter Automobile Receivables Trust 2024-2", "Deep subprime"),
    ];

    private ReportGenerator Generator() =>
        new(new PoolReportRepository(postgres.DataSource), NullLogger<ReportGenerator>.Instance);

    private async Task SeedAsync()
    {
        await postgres.ResetAsync();
        var migration = new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance)
            .Upgrade(postgres.ConnectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());

        await using var connection = await postgres.DataSource.OpenConnectionAsync();

        // Two trusts over two periods, with the shapes that matter: a closed loan, a loan with
        // no delinquency status, and a null credit score.
        await connection.ExecuteAsync("""
            insert into auto_loan_asset_data
                (trust_cik, reporting_period_end, asset_number, original_loan_amount,
                 reporting_period_interest_rate, obligor_credit_score, beginning_balance,
                 ending_balance, remaining_term_to_maturity, current_delinquency_days,
                 charged_off_principal_amount, recovered_amount, zero_balance_code)
            values
                ('2099048', date '2026-07-31', 'p1', 20000, 0.0650, 770, 10000, 9500, 36,    0, 0, 0, null),
                ('2099048', date '2026-07-31', 'p2', 20000, 0.0700, null, 8000, 7500, 30,   35, 0, 0, null),
                ('2099048', date '2026-08-31', 'p1', 20000, 0.0650, 770,  9500, 9000, 35,    0, 0, 0, null),
                ('2099048', date '2026-08-31', 'p2', 20000, 0.0700, null, 7500, 7000, 29, null, 0, 0, null),
                ('2013062', date '2026-07-31', 's1', 15000, 0.2500, 560,  5000,    0,  0, null, 4000, 500, 4),
                ('2013062', date '2026-07-31', 's2', 15000, 0.2600, 575,  6000, 5500, 24,   95, 0, 0, null),
                ('2013062', date '2026-08-31', 's2', 15000, 0.2600, 575,  5500, 5000, 23,  120, 0, 0, null);

            insert into treasury_par_yields (quote_date, tenor_months, tenor_label, rate_percent)
            values (date '2026-07-31', 24, '2Y', 4.28), (date '2026-07-31', 36, '3Y', 4.34),
                   (date '2026-08-31', 24, '2Y', 4.34), (date '2026-08-31', 36, '3Y', 4.40);

            insert into ingestion_runs
                (provider_name, asset_class, source_id, sha256, subject_id, reporting_period,
                 status, started_at, completed_at, records_loaded)
            values
                ('SecAbsEe', 'AutoLoanABS', 'acc-1', 'aaaaaaaaaaaaaaaa', '2099048',
                 date '2026-08-31', 1, now() - interval '1 hour', now(), 4),
                ('TreasuryYields', 'USTreasuries', 'ust-2026', 'bbbbbbbbbbbbbbbb', 'UST',
                 date '2026-12-31', 1, now() - interval '1 hour', now(), 4)
            """);
    }

    [Fact]
    public async Task Builds_the_report_from_the_database()
    {
        await SeedAsync();

        var data = await Generator().BuildAsync(Trusts, CancellationToken.None);

        Assert.Equal(2, data.Trusts.Count);
        Assert.Equal(2, data.Provenance.Count);
        Assert.Equal(new DateOnly(2026, 8, 31), data.TreasuryAsOf);

        // Configuration order is preserved so the page is stable between runs.
        Assert.Equal("2099048", data.Trusts[0].Cik);
        Assert.Equal("Prime", data.Trusts[0].CreditTier);

        // Periods are oldest first so charts read left to right.
        var prime = data.Trusts[0];
        Assert.Equal(2, prime.Periods.Count);
        Assert.Equal(new DateOnly(2026, 7, 31), prime.Periods[0].ReportingPeriodEnd);
        Assert.Equal(new DateOnly(2026, 8, 31), prime.Latest!.ReportingPeriodEnd);
    }

    [Fact]
    public async Task Metrics_survive_the_round_trip_from_sql()
    {
        await SeedAsync();

        var data = await Generator().BuildAsync(Trusts, CancellationToken.None);
        var latest = data.Trusts[0].Latest!;

        // (9000 x 0.0650 + 7000 x 0.0700) / 16000 x 100 = (585 + 490) / 16000 x 100 = 6.71875%
        Assert.Equal(6.7188m, Math.Round(latest.WaInterestRatePercent!.Value, 4));

        // Only p1 has a score: 9000 x 770 / 9000 = 770
        Assert.Equal(770m, Math.Round(latest.WaCreditScore!.Value, 4));

        // p2 reports no status in 2026-08, so it leaves the delinquency denominator.
        Assert.Equal(1, latest.ActiveWithoutDelinquencyStatus);
        Assert.Equal(0m, latest.Dq30PlusPercent);

        // The 3Y point is nearest the pool's weighted-average remaining term.
        Assert.Equal("3Y", latest.TreasuryTenor);
        Assert.Equal(4.40m, latest.TreasuryRatePercent);
        Assert.Equal(2.3188m, Math.Round(latest.IndicativeSpreadPercent!.Value, 4));
    }

    [Fact]
    public async Task Writes_an_index_and_a_csv_per_trust()
    {
        await SeedAsync();
        var data = await Generator().BuildAsync(Trusts, CancellationToken.None);

        var written = await Generator().WriteAsync(data, _output, CancellationToken.None);

        Assert.Equal(3, written.Count);
        Assert.True(File.Exists(Path.Combine(_output, "index.html")));
        Assert.True(File.Exists(Path.Combine(_output, "pool-performance-2099048.csv")));
        Assert.True(File.Exists(Path.Combine(_output, "pool-performance-2013062.csv")));

        var html = await File.ReadAllTextAsync(Path.Combine(_output, "index.html"));
        Assert.DoesNotContain("NaN", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<td></td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);

        // Provenance really reaches the page, which is what makes a figure traceable.
        Assert.Contains("aaaaaaaaaaaa", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_csv_carries_a_header_and_one_row_per_period()
    {
        await SeedAsync();
        var data = await Generator().BuildAsync(Trusts, CancellationToken.None);
        await Generator().WriteAsync(data, _output, CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(Path.Combine(_output, "pool-performance-2099048.csv"));

        Assert.Equal(3, lines.Length);  // header plus two periods
        Assert.StartsWith("trust_cik,trust_name,credit_tier,reporting_period_end", lines[0], StringComparison.Ordinal);
        Assert.Contains("2099048", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_trust_with_data_but_no_configuration_still_appears()
    {
        await SeedAsync();

        // Only the prime trust is configured; the subprime one is not.
        var data = await Generator().BuildAsync([Trusts[0]], CancellationToken.None);

        Assert.Equal(2, data.Trusts.Count);
        var unconfigured = data.Trusts.Single(t => t.Cik == "2013062");

        // Vanishing from a report that claims to cover the data would be worse than a label.
        Assert.Equal("Unconfigured", unconfigured.CreditTier);
        Assert.Equal("CIK 2013062", unconfigured.Name);
    }

    [Fact]
    public async Task A_configured_trust_with_no_data_is_left_out()
    {
        await SeedAsync();

        var data = await Generator().BuildAsync(
            [.. Trusts, new TrustDescriptor("9999999", "Nonexistent Trust", "Prime")],
            CancellationToken.None);

        Assert.Equal(2, data.Trusts.Count);
        Assert.DoesNotContain(data.Trusts, t => t.Cik == "9999999");
    }

    [Fact]
    public async Task An_empty_database_produces_no_trusts_rather_than_an_empty_page()
    {
        await postgres.ResetAsync();
        Assert.True(new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance)
            .Upgrade(postgres.ConnectionString).Successful);

        var data = await Generator().BuildAsync(Trusts, CancellationToken.None);

        // The CLI turns this into a non-zero exit and "Run `ingest` before `report`".
        Assert.Empty(data.Trusts);
        Assert.Null(data.TreasuryAsOf);
    }

    [Fact]
    public async Task Csv_values_are_unrounded_so_the_figures_can_be_rechecked()
    {
        await SeedAsync();
        var data = await Generator().BuildAsync(Trusts, CancellationToken.None);
        await Generator().WriteAsync(data, _output, CancellationToken.None);

        var csv = await File.ReadAllTextAsync(Path.Combine(_output, "pool-performance-2099048.csv"));

        // The HTML rounds to 6.72% for reading; the CSV keeps what the SQL returned.
        Assert.Contains("6.71875", csv, StringComparison.Ordinal);
    }
}
