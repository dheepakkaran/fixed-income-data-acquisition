namespace DataAcquisition.Reports.Model;

/// <summary>
/// One trust's metrics for one reporting period, from <c>v_pool_period</c> joined to
/// <c>v_pool_spread</c> and <c>v_pool_cumulative_loss</c>. Formulae are in docs/metrics.md.
/// </summary>
public sealed record PoolPeriodMetrics
{
    public required string TrustCik { get; init; }
    public required DateOnly ReportingPeriodEnd { get; init; }

    public long LoansReported { get; init; }
    public long LoansActive { get; init; }
    public decimal PoolBalance { get; init; }
    public decimal OriginalPoolBalance { get; init; }

    /// <summary>Percentage. ABS-EE stores fractions; the view multiplies by 100.</summary>
    public decimal? WaInterestRatePercent { get; init; }

    public decimal? WaCreditScore { get; init; }
    public decimal? WaRemainingTermMonths { get; init; }

    public decimal? Dq30PlusPercent { get; init; }
    public decimal? Dq60PlusPercent { get; init; }
    public decimal? Dq90PlusPercent { get; init; }

    /// <summary>Active loans whose delinquency status the filing omits, excluded from the rates above.</summary>
    public long ActiveWithoutDelinquencyStatus { get; init; }

    public long LoansPaidOff { get; init; }
    public decimal? PayoffRatePercent { get; init; }
    public long LoansChargedOff { get; init; }

    public decimal ChargedOffPrincipal { get; init; }
    public decimal Recovered { get; init; }
    public decimal NetLoss { get; init; }
    public decimal CumulativeNetLoss { get; init; }
    public decimal? CumulativeNetLossPercent { get; init; }

    public DateOnly? TreasuryQuoteDate { get; init; }
    public string? TreasuryTenor { get; init; }
    public decimal? TreasuryRatePercent { get; init; }
    public decimal? IndicativeSpreadPercent { get; init; }
}
