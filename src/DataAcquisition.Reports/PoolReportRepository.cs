using Dapper;
using DataAcquisition.Reports.Model;
using Npgsql;

namespace DataAcquisition.Reports;

/// <summary>
/// Reads the report's figures out of the views in
/// <c>0004_pool_performance_views.sql</c>. The SQL is written out in full so the queries
/// behind published numbers stay reviewable.
/// </summary>
public sealed class PoolReportRepository(NpgsqlDataSource dataSource)
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private const string MetricsSql = """
        select
            p.trust_cik                         as TrustCik,
            p.reporting_period_end              as ReportingPeriodEnd,
            p.loans_reported                    as LoansReported,
            p.loans_active                      as LoansActive,
            p.pool_balance                      as PoolBalance,
            p.original_pool_balance             as OriginalPoolBalance,
            p.wa_interest_rate_percent          as WaInterestRatePercent,
            p.wa_credit_score                   as WaCreditScore,
            p.wa_remaining_term_months          as WaRemainingTermMonths,
            p.dq_30_plus_percent                as Dq30PlusPercent,
            p.dq_60_plus_percent                as Dq60PlusPercent,
            p.dq_90_plus_percent                as Dq90PlusPercent,
            p.active_without_delinquency_status as ActiveWithoutDelinquencyStatus,
            p.loans_paid_off                    as LoansPaidOff,
            p.payoff_rate_percent               as PayoffRatePercent,
            p.loans_charged_off                 as LoansChargedOff,
            p.charged_off_principal             as ChargedOffPrincipal,
            p.recovered                         as Recovered,
            p.net_loss                          as NetLoss,
            l.cumulative_net_loss               as CumulativeNetLoss,
            l.cumulative_net_loss_percent       as CumulativeNetLossPercent,
            s.treasury_quote_date               as TreasuryQuoteDate,
            s.treasury_tenor                    as TreasuryTenor,
            s.treasury_rate_percent             as TreasuryRatePercent,
            s.indicative_spread_percent         as IndicativeSpreadPercent
        from v_pool_period p
        join v_pool_cumulative_loss l
          on l.trust_cik = p.trust_cik
         and l.reporting_period_end = p.reporting_period_end
        left join v_pool_spread s
          on s.trust_cik = p.trust_cik
         and s.reporting_period_end = p.reporting_period_end
        order by p.trust_cik, p.reporting_period_end
        """;

    /// <summary>All pool metrics, oldest period first so charts read left to right.</summary>
    public async Task<IReadOnlyList<PoolPeriodMetrics>> GetMetricsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = await connection.QueryAsync<PoolPeriodMetrics>(
            new CommandDefinition(MetricsSql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows];
    }

    /// <summary>
    /// The successful runs behind the figures, newest first. Cited on the page so every
    /// number is traceable to the exhibit it came from.
    /// </summary>
    public async Task<IReadOnlyList<RunProvenance>> GetProvenanceAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select
                id               as RunId,
                provider_name    as Provider,
                subject_id       as SubjectId,
                reporting_period as ReportingPeriod,
                completed_at     as CompletedAt,
                records_loaded   as RecordsLoaded,
                sha256           as Sha256
            from ingestion_runs
            where status = 1
            order by reporting_period desc nulls last, provider_name, subject_id
            """;

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = await connection.QueryAsync<RunProvenance>(
            new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows];
    }

    /// <summary>Latest Treasury curve date held, shown as the benchmark's freshness.</summary>
    public async Task<DateOnly?> GetTreasuryAsOfAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<DateOnly?>(new CommandDefinition(
            "select max(quote_date) from treasury_par_yields",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
