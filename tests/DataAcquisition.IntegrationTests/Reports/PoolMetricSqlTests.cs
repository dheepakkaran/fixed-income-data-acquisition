using Dapper;
using DataAcquisition.IntegrationTests.Infrastructure;
using DataAcquisition.Reports;
using DataAcquisition.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataAcquisition.IntegrationTests.Reports;

/// <summary>
/// The metric SQL against a five-loan pool small enough to work out by hand. Every expected
/// value below is derived in the comment beside it, so a disagreement says which of the two
/// is wrong rather than just that they differ.
///
/// <code>
/// asset  original  rate  score  opening  ending  term  dq_days  zb  charged_off  recovered
/// A        20,000  0.10    700   10,000   9,000    24        0   -            0          0
/// B        30,000  0.20    600   20,000  19,000    36       45   -            0          0
/// C        10,000  0.05   null    5,000   1,000    12       95   -            0          0
/// D        15,000  0.15    650    8,000       0     0     null   1            0          0   paid off
/// E        25,000  0.25    550   12,000       0     0     null   4        9,000      2,000   charged off
/// </code>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PoolMetricSqlTests(PostgresFixture postgres)
{
    private const string Trust = "TEST";
    private static readonly DateOnly Period = new(2026, 8, 31);

    private async Task SeedAsync()
    {
        await postgres.ResetAsync();
        var migration = new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance)
            .Upgrade(postgres.ConnectionString);
        Assert.True(migration.Successful, migration.Error?.ToString());

        await using var connection = await postgres.DataSource.OpenConnectionAsync();

        await connection.ExecuteAsync("""
            insert into auto_loan_asset_data
                (trust_cik, reporting_period_end, asset_number, reporting_period_begin,
                 original_loan_amount, original_loan_term, original_interest_rate,
                 reporting_period_interest_rate, obligor_credit_score,
                 beginning_balance, ending_balance, remaining_term_to_maturity,
                 current_delinquency_days, charged_off_principal_amount, recovered_amount,
                 zero_balance_code)
            values
                ('TEST', date '2026-08-31', 'A', date '2026-08-01', 20000, 24, 0.10, 0.10,  700, 10000,  9000, 24,    0,    0,    0, null),
                ('TEST', date '2026-08-31', 'B', date '2026-08-01', 30000, 36, 0.20, 0.20,  600, 20000, 19000, 36,   45,    0,    0, null),
                ('TEST', date '2026-08-31', 'C', date '2026-08-01', 10000, 12, 0.05, 0.05, null,  5000,  1000, 12,   95,    0,    0, null),
                ('TEST', date '2026-08-31', 'D', date '2026-08-01', 15000, 48, 0.15, 0.15,  650,  8000,     0,  0, null,    0,    0,    1),
                ('TEST', date '2026-08-31', 'E', date '2026-08-01', 25000, 60, 0.25, 0.25,  550, 12000,     0,  0, null, 9000, 2000,    4)
            """);

        // A curve with a 2Y and a 3Y point, so the nearest-tenor match has a real choice.
        await connection.ExecuteAsync("""
            insert into treasury_par_yields (quote_date, tenor_months, tenor_label, rate_percent)
            values (date '2026-08-31', 24, '2Y', 4.00),
                   (date '2026-08-31', 36, '3Y', 4.40)
            """);
    }

    private async Task<dynamic> PeriodRowAsync()
    {
        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync(
            "select * from v_pool_period where trust_cik = @Trust", new { Trust });
    }

    [Fact]
    public async Task Counts_and_balances_match_the_hand_calculation()
    {
        await SeedAsync();
        var row = await PeriodRowAsync();

        // Five loans reported; three still carry a balance (A, B, C).
        Assert.Equal(5L, (long)row.loans_reported);
        Assert.Equal(3L, (long)row.loans_active);

        // 9,000 + 19,000 + 1,000 + 0 + 0 = 29,000
        Assert.Equal(29_000m, (decimal)row.pool_balance);

        // 10,000 + 20,000 + 5,000 + 8,000 + 12,000 = 55,000
        Assert.Equal(55_000m, (decimal)row.pool_balance_opening);

        // 20,000 + 30,000 + 10,000 + 15,000 + 25,000 = 100,000
        Assert.Equal(100_000m, (decimal)row.original_pool_balance);
    }

    [Fact]
    public async Task Weighted_average_rate_matches_the_hand_calculation()
    {
        await SeedAsync();
        var row = await PeriodRowAsync();

        // Balance-weighted over the ACTIVE pool only, x100 because the stored rate is a
        // fraction:
        //   (9,000 x 0.10) + (19,000 x 0.20) + (1,000 x 0.05) = 900 + 3,800 + 50 = 4,750
        //   4,750 / 29,000 = 0.16379310344827586...
        //   x 100 = 16.3793%
        // The paid-off and charged-off loans carry 0.15 and 0.25 and are excluded; including
        // them would pull this toward 17.4%.
        Assert.Equal(16.3793m, Math.Round((decimal)row.wa_interest_rate_percent, 4));
    }

    [Fact]
    public async Task Weighted_average_credit_score_excludes_loans_without_one()
    {
        await SeedAsync();
        var row = await PeriodRowAsync();

        // Only A and B have a score among the active loans; C's is null and is excluded from
        // BOTH numerator and denominator:
        //   (9,000 x 700) + (19,000 x 600) = 6,300,000 + 11,400,000 = 17,700,000
        //   17,700,000 / (9,000 + 19,000) = 17,700,000 / 28,000 = 632.142857...
        // Treating C's null as a zero score would give 17,700,000 / 29,000 = 610.3, which is
        // the error this excludes.
        Assert.Equal(632.1429m, Math.Round((decimal)row.wa_credit_score, 4));
    }

    [Fact]
    public async Task Weighted_average_remaining_term_matches_the_hand_calculation()
    {
        await SeedAsync();
        var row = await PeriodRowAsync();

        //   (9,000 x 24) + (19,000 x 36) + (1,000 x 12) = 216,000 + 684,000 + 12,000 = 912,000
        //   912,000 / 29,000 = 31.4482758620689655...
        Assert.Equal(31.4483m, Math.Round((decimal)row.wa_remaining_term_months, 4));
    }

    [Fact]
    public async Task Delinquency_buckets_are_day_thresholds_and_nest()
    {
        await SeedAsync();
        var row = await PeriodRowAsync();

        // Denominator is the active balance that reports a status: A(0), B(45), C(95),
        // so 9,000 + 19,000 + 1,000 = 29,000. D and E report no status but are also closed.
        //   30+: B(45) and C(95)  -> 19,000 + 1,000 = 20,000 -> 20,000/29,000 = 68.9655%
        //   60+: C(95) only       ->           1,000         ->  1,000/29,000 =  3.4483%
        //   90+: C(95) only       ->           1,000         ->  3.4483%
        // 60+ and 90+ are equal because C at 95 days is the only loan past either, which is
        // what "nested buckets" means.
        Assert.Equal(68.9655m, Math.Round((decimal)row.dq_30_plus_percent, 4));
        Assert.Equal(3.4483m, Math.Round((decimal)row.dq_60_plus_percent, 4));
        Assert.Equal(3.4483m, Math.Round((decimal)row.dq_90_plus_percent, 4));

        Assert.Equal(0L, (long)row.active_without_delinquency_status);
    }

    [Fact]
    public async Task Delinquency_denominator_excludes_active_loans_with_no_status()
    {
        await SeedAsync();

        // Add an active loan that reports no delinquency status at all, which real filings do
        // on up to 5% of records.
        await using (var connection = await postgres.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                insert into auto_loan_asset_data
                    (trust_cik, reporting_period_end, asset_number, beginning_balance,
                     ending_balance, current_delinquency_days, reporting_period_interest_rate)
                values ('TEST', date '2026-08-31', 'F', 71000, 71000, null, 0.10)
                """);
        }

        var row = await PeriodRowAsync();

        // F adds 71,000 of active balance but no status, so the delinquency denominator stays
        // 29,000 and 30+ stays 68.9655%. Had F been counted as current, the denominator would
        // be 100,000 and 30+ would read 20.0% - a loan with no reported status silently
        // improving the pool by two thirds.
        Assert.Equal(68.9655m, Math.Round((decimal)row.dq_30_plus_percent, 4));
        Assert.Equal(1L, (long)row.active_without_delinquency_status);
        Assert.Equal(4L, (long)row.loans_active);
    }

    [Fact]
    public async Task Payoff_rate_uses_the_opening_balance()
    {
        await SeedAsync();
        var row = await PeriodRowAsync();

        // D is the only loan with zeroBalanceCode 1 (Prepaid or Matured). It ends at zero, so
        // an ending-balance numerator would read 0%. Its OPENING balance is what left:
        //   8,000 / 55,000 = 14.5455%
        Assert.Equal(1L, (long)row.loans_paid_off);
        Assert.Equal(14.5455m, Math.Round((decimal)row.payoff_rate_percent, 4));
    }

    [Fact]
    public async Task Net_loss_is_charge_offs_less_recoveries()
    {
        await SeedAsync();
        var row = await PeriodRowAsync();

        // E alone: charged off 9,000, recovered 2,000.
        Assert.Equal(1L, (long)row.loans_charged_off);
        Assert.Equal(9_000m, (decimal)row.charged_off_principal);
        Assert.Equal(2_000m, (decimal)row.recovered);
        Assert.Equal(7_000m, (decimal)row.net_loss);
    }

    [Fact]
    public async Task A_reversed_charge_off_nets_out_across_periods()
    {
        await SeedAsync();

        // The real pattern: charged off, reversed the next month, charged off again. One
        // AmeriCredit loan reads exactly this. Summing the periods must give one charge-off,
        // which is why negatives are not filtered out.
        await using (var connection = await postgres.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("""
                insert into auto_loan_asset_data
                    (trust_cik, reporting_period_end, asset_number, original_loan_amount,
                     beginning_balance, ending_balance, charged_off_principal_amount,
                     recovered_amount, zero_balance_code)
                values
                    ('REV', date '2026-06-30', 'R', 20000, 5000, 0,  5000, 0,    4),
                    ('REV', date '2026-07-31', 'R', 20000, 0, 5000, -5000, 0, null),
                    ('REV', date '2026-08-31', 'R', 20000, 5000, 0,  5000, 0,    4)
                """);
        }

        await using var read = await postgres.DataSource.OpenConnectionAsync();
        var cumulative = await read.QuerySingleAsync<decimal>("""
            select cumulative_net_loss from v_pool_cumulative_loss
            where trust_cik = 'REV' and reporting_period_end = date '2026-08-31'
            """);

        // 5,000 - 5,000 + 5,000 = 5,000, not 15,000.
        Assert.Equal(5_000m, cumulative);
    }

    [Fact]
    public async Task Cumulative_loss_is_a_percentage_of_the_original_pool()
    {
        await SeedAsync();

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync(
            "select * from v_pool_cumulative_loss where trust_cik = @Trust", new { Trust });

        // 7,000 net loss over a 100,000 original pool = 7.0%
        Assert.Equal(7_000m, (decimal)row.cumulative_net_loss);
        Assert.Equal(7.0m, Math.Round((decimal)row.cumulative_net_loss_percent, 4));
    }

    [Fact]
    public async Task The_spread_picks_the_nearest_tenor_and_subtracts_like_for_like()
    {
        await SeedAsync();

        await using var connection = await postgres.DataSource.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync(
            "select * from v_pool_spread where trust_cik = @Trust", new { Trust });

        // WA remaining term is 31.4483 months. |31.4483 - 24| = 7.4483 and
        // |31.4483 - 36| = 4.5517, so the 3Y point wins.
        Assert.Equal("3Y", (string)row.treasury_tenor);
        Assert.Equal(4.40m, (decimal)row.treasury_rate_percent);

        // 16.3793% pool rate less 4.40% Treasury = 11.9793%. Both legs are percentages:
        // the view has already multiplied the stored fraction by 100.
        Assert.Equal(11.9793m, Math.Round((decimal)row.indicative_spread_percent, 4));
    }

    [Fact]
    public async Task The_spread_falls_back_to_the_last_curve_before_a_non_business_day()
    {
        await postgres.ResetAsync();
        Assert.True(new DatabaseMigrator(NullLogger<DatabaseMigrator>.Instance)
            .Upgrade(postgres.ConnectionString).Successful);

        await using var connection = await postgres.DataSource.OpenConnectionAsync();

        // 2026-05-31 is a Sunday: Treasury publishes nothing. The last curve is Friday the 29th.
        await connection.ExecuteAsync("""
            insert into auto_loan_asset_data
                (trust_cik, reporting_period_end, asset_number, beginning_balance,
                 ending_balance, remaining_term_to_maturity, reporting_period_interest_rate)
            values ('WKND', date '2026-05-31', 'A', 1000, 1000, 36, 0.10);

            insert into treasury_par_yields (quote_date, tenor_months, tenor_label, rate_percent)
            values (date '2026-05-29', 36, '3Y', 4.10)
            """);

        var row = await connection.QuerySingleAsync(
            "select * from v_pool_spread where trust_cik = 'WKND'");

        Assert.Equal(new DateOnly(2026, 5, 29), (DateOnly)row.treasury_quote_date);
        Assert.Equal(5.90m, Math.Round((decimal)row.indicative_spread_percent, 4)); // 10.00 - 4.10
    }
}
