using System.Globalization;
using System.Text;
using DataAcquisition.Reports.Model;

namespace DataAcquisition.Reports.Rendering;

/// <summary>
/// One CSV per trust, so the figures can be checked or re-analysed without the database.
/// Values are written unrounded: the HTML rounds for reading, the CSV keeps what the SQL
/// returned.
/// </summary>
public static class CsvReportWriter
{
    private static readonly string[] Header =
    [
        "trust_cik", "trust_name", "credit_tier", "reporting_period_end",
        "loans_reported", "loans_active", "pool_balance", "original_pool_balance",
        "wa_interest_rate_percent", "wa_credit_score", "wa_remaining_term_months",
        "dq_30_plus_percent", "dq_60_plus_percent", "dq_90_plus_percent",
        "active_without_delinquency_status",
        "loans_paid_off", "payoff_rate_percent", "loans_charged_off",
        "charged_off_principal", "recovered", "net_loss",
        "cumulative_net_loss", "cumulative_net_loss_percent",
        "treasury_quote_date", "treasury_tenor", "treasury_rate_percent",
        "indicative_spread_percent",
    ];

    public static string Render(TrustReport trust)
    {
        ArgumentNullException.ThrowIfNull(trust);

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(",", Header));

        foreach (var p in trust.Periods.OrderBy(p => p.ReportingPeriodEnd))
        {
            csv.AppendLine(string.Join(",",
            [
                Quote(trust.Cik), Quote(trust.Name), Quote(trust.CreditTier),
                Date(p.ReportingPeriodEnd),
                Int(p.LoansReported), Int(p.LoansActive),
                Dec(p.PoolBalance), Dec(p.OriginalPoolBalance),
                Dec(p.WaInterestRatePercent), Dec(p.WaCreditScore), Dec(p.WaRemainingTermMonths),
                Dec(p.Dq30PlusPercent), Dec(p.Dq60PlusPercent), Dec(p.Dq90PlusPercent),
                Int(p.ActiveWithoutDelinquencyStatus),
                Int(p.LoansPaidOff), Dec(p.PayoffRatePercent), Int(p.LoansChargedOff),
                Dec(p.ChargedOffPrincipal), Dec(p.Recovered), Dec(p.NetLoss),
                Dec(p.CumulativeNetLoss), Dec(p.CumulativeNetLossPercent),
                Date(p.TreasuryQuoteDate), Quote(p.TreasuryTenor), Dec(p.TreasuryRatePercent),
                Dec(p.IndicativeSpreadPercent),
            ]));
        }

        return csv.ToString();
    }

    /// <summary>A file name safe on every platform, derived from the trust's CIK.</summary>
    public static string FileNameFor(TrustReport trust)
    {
        ArgumentNullException.ThrowIfNull(trust);
        return $"pool-performance-{trust.Cik}.csv";
    }

    private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Dec(decimal? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Date(DateOnly? value) =>
        value is { } d && d != default ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>RFC 4180 quoting: wrap in quotes and double any embedded quote.</summary>
    private static string Quote(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains(',', StringComparison.Ordinal)
            || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }
}
