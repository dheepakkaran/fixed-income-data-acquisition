using System.Globalization;
using System.Text;
using DataAcquisition.Reports.Model;

namespace DataAcquisition.Reports.Rendering;

/// <summary>
/// Renders the ABS pool performance report as a single static HTML page: plain HTML, inline
/// CSS, inline SVG charts drawn by <see cref="SvgLineChart"/>. No JavaScript and no frontend
/// framework, because the page is read, not interacted with, and GitHub Pages serves it as
/// a file.
/// </summary>
public static class HtmlReportWriter
{
    private static readonly string[] Palette =
    [
        "#1f6feb", "#d29922", "#cf222e", "#2da44e", "#8250df", "#0969da",
    ];

    public static string Render(ReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var html = new StringBuilder();
        html.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>ABS Pool Performance Report</title>
            """);
        html.Append(Style());
        html.Append("</head><body><main>");

        html.Append(Header(data));
        html.Append(SummarySection(data));

        foreach (var (trust, index) in data.Trusts.Select((t, i) => (t, i)))
        {
            html.Append(TrustSection(trust, index));
        }

        html.Append(ComparisonSection(data));
        html.Append(MethodologySection());
        html.Append(ProvenanceSection(data));

        html.Append("</main></body></html>");
        return html.ToString();
    }

    private static string Style() => """
        <style>
        :root {
          --bg: #ffffff; --fg: #1f2328; --muted: #656d76; --line: #d1d9e0;
          --accent: #1f6feb; --warn-bg: #fff8c5; --warn-line: #d4a72c; --head: #f6f8fa;
        }
        * { box-sizing: border-box; }
        body {
          margin: 0; background: var(--bg); color: var(--fg);
          font: 15px/1.6 -apple-system, BlinkMacSystemFont, "Segoe UI", Helvetica, Arial, sans-serif;
        }
        main { max-width: 1040px; margin: 0 auto; padding: 32px 20px 64px; }
        h1 { font-size: 28px; margin: 0 0 4px; letter-spacing: -0.02em; }
        h2 { font-size: 20px; margin: 44px 0 4px; padding-top: 20px; border-top: 1px solid var(--line); }
        h3 { font-size: 15px; margin: 24px 0 8px; color: var(--muted); text-transform: uppercase;
             letter-spacing: 0.06em; font-weight: 600; }
        p { margin: 8px 0; }
        a { color: var(--accent); }
        .sub { color: var(--muted); margin: 0 0 20px; }
        .tier { display: inline-block; font-size: 12px; font-weight: 600; padding: 2px 8px;
                border: 1px solid var(--line); border-radius: 999px; color: var(--muted);
                vertical-align: middle; margin-left: 8px; }
        .scroller { overflow-x: auto; margin: 12px 0 8px; -webkit-overflow-scrolling: touch; }
        table { border-collapse: collapse; width: 100%; font-variant-numeric: tabular-nums; }
        .scroller table { min-width: 760px; }
        .scroller td:first-child, .scroller th:first-child { min-width: 180px; }
        caption { text-align: left; color: var(--muted); font-size: 13px; padding-bottom: 6px; }
        th, td { padding: 7px 10px; border-bottom: 1px solid var(--line); text-align: right; }
        th:first-child, td:first-child { text-align: left; }
        thead th { background: var(--head); font-size: 12px; text-transform: uppercase;
                   letter-spacing: 0.04em; color: var(--muted); border-bottom: 1px solid var(--line); }
        tbody tr:last-child td { border-bottom: none; }
        .note { background: var(--warn-bg); border: 1px solid var(--warn-line); border-radius: 6px;
                padding: 12px 14px; margin: 16px 0; font-size: 14px; }
        .note strong { display: block; margin-bottom: 4px; }
        .chart { margin: 20px 0 8px; }
        .chart svg { width: 100%; height: auto; display: block; border: 1px solid var(--line); border-radius: 6px; }
        figcaption { color: var(--muted); font-size: 13px; margin-top: 6px; }
        .legend { list-style: none; display: flex; flex-wrap: wrap; gap: 14px; padding: 8px 0 0; margin: 0;
                  font-size: 13px; color: var(--muted); }
        .legend li { display: flex; align-items: center; gap: 6px; }
        .swatch { width: 11px; height: 11px; border-radius: 2px; display: inline-block; }
        .grid { stroke: #e6eaef; stroke-width: 1; }
        .axis { fill: #656d76; font-size: 11px; font-family: inherit; }
        code { background: var(--head); padding: 1px 5px; border-radius: 4px; font-size: 13px; }
        .empty { color: var(--muted); font-style: italic; }
        .foot { color: var(--muted); font-size: 13px; margin-top: 32px; }
        @media (prefers-color-scheme: dark) {
          :root { --bg:#0d1117; --fg:#e6edf3; --muted:#9198a1; --line:#30363d;
                  --accent:#4493f8; --warn-bg:#272115; --warn-line:#9e6a03; --head:#161b22; }
          .grid { stroke: #21262d; }
          .axis { fill: #9198a1; }
        }
        </style>
        """;

    private static string Header(ReportData data)
    {
        var latest = data.Trusts
            .SelectMany(t => t.Periods)
            .Select(p => p.ReportingPeriodEnd)
            .DefaultIfEmpty()
            .Max();

        return $"""
            <h1>ABS Pool Performance Report</h1>
            <p class="sub">
              Loan-level auto loan ABS from SEC EDGAR Form ABS-EE, benchmarked against the
              U.S. Treasury daily par yield curve.
            </p>
            <div class="scroller"><table>
              <caption>Data freshness</caption>
              <tbody>
                <tr><td>Latest ABS-EE reporting period</td><td>{Fmt(latest)}</td></tr>
                <tr><td>Treasury curve as of</td><td>{Fmt(data.TreasuryAsOf)}</td></tr>
                <tr><td>Report generated</td><td>{Escape(data.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture))}</td></tr>
                <tr><td>Trusts covered</td><td>{data.Trusts.Count}</td></tr>
                <tr><td>Ingestion runs behind these figures</td><td>{data.Provenance.Count}</td></tr>
              </tbody>
            </table></div>
            """;
    }

    private static string SummarySection(ReportData data)
    {
        var rows = new StringBuilder();
        foreach (var trust in data.Trusts)
        {
            if (trust.Latest is not { } p)
            {
                continue;
            }

            rows.Append(CultureInfo.InvariantCulture, $"""
                <tr>
                  <td>{Escape(trust.Name)}<span class="tier">{Escape(trust.CreditTier)}</span></td>
                  <td>{Fmt(p.ReportingPeriodEnd)}</td>
                  <td>{Money(p.PoolBalance)}</td>
                  <td>{p.LoansActive:N0}</td>
                  <td>{Pct(p.WaInterestRatePercent)}</td>
                  <td>{Num(p.WaCreditScore, 0)}</td>
                  <td>{Pct(p.Dq30PlusPercent)}</td>
                  <td>{Pct(p.Dq90PlusPercent)}</td>
                  <td>{Pct(p.CumulativeNetLossPercent, 3)}</td>
                  <td>{Pct(p.IndicativeSpreadPercent)}</td>
                </tr>
                """);
        }

        return $"""
            <h2>Summary</h2>
            <div class="scroller"><table>
              <caption>Most recent reporting period for each trust. Averages are weighted by ending balance over loans still carrying one.</caption>
              <thead><tr>
                <th>Trust</th><th>Period</th><th>Pool balance</th><th>Active loans</th>
                <th>WA rate</th><th>WA score</th><th>30+ DQ</th><th>90+ DQ</th>
                <th>Cum. net loss</th><th>Ind. spread</th>
              </tr></thead>
              <tbody>{rows}</tbody>
            </table></div>
            """;
    }

    private static string TrustSection(TrustReport trust, int index)
    {
        var colour = Palette[index % Palette.Length];
        var labels = trust.Periods.Select(p => p.ReportingPeriodEnd.ToString("MMM yy", CultureInfo.InvariantCulture)).ToArray();

        var rows = new StringBuilder();
        foreach (var p in trust.Periods.OrderByDescending(p => p.ReportingPeriodEnd))
        {
            rows.Append(CultureInfo.InvariantCulture, $"""
                <tr>
                  <td>{Fmt(p.ReportingPeriodEnd)}</td>
                  <td>{Money(p.PoolBalance)}</td>
                  <td>{p.LoansActive:N0} / {p.LoansReported:N0}</td>
                  <td>{Pct(p.WaInterestRatePercent)}</td>
                  <td>{Num(p.WaCreditScore, 0)}</td>
                  <td>{Num(p.WaRemainingTermMonths, 1)}</td>
                  <td>{Pct(p.Dq30PlusPercent)}</td>
                  <td>{Pct(p.Dq60PlusPercent)}</td>
                  <td>{Pct(p.Dq90PlusPercent)}</td>
                  <td>{Pct(p.PayoffRatePercent)}</td>
                  <td>{Money(p.NetLoss)}</td>
                  <td>{Pct(p.CumulativeNetLossPercent, 3)}</td>
                </tr>
                """);
        }

        var id = $"t{index}";
        var charts = new StringBuilder();

        charts.Append(SvgLineChart.Render(
            $"{id}-bal", $"{trust.Name}: pool balance", "",
            [new ChartSeries("Pool balance ($M)", colour,
                [.. trust.Periods.Select((p, i) => (labels[i], (decimal?)(p.PoolBalance / 1_000_000m)))])]));

        charts.Append(SvgLineChart.Render(
            $"{id}-dq", $"{trust.Name}: delinquency by balance", "%",
            [
                new ChartSeries("30+ days", Palette[2],
                    [.. trust.Periods.Select((p, i) => (labels[i], p.Dq30PlusPercent))]),
                new ChartSeries("60+ days", Palette[1],
                    [.. trust.Periods.Select((p, i) => (labels[i], p.Dq60PlusPercent))]),
                new ChartSeries("90+ days", Palette[4],
                    [.. trust.Periods.Select((p, i) => (labels[i], p.Dq90PlusPercent))]),
            ]));

        return $"""
            <h2>{Escape(trust.Name)}<span class="tier">{Escape(trust.CreditTier)}</span></h2>
            <p class="sub">CIK {Escape(trust.Cik)} &middot; {trust.Periods.Count} reporting period(s) held</p>
            <div class="scroller"><table>
              <caption>Per reporting period. Active loans are those with a positive ending balance; closed loans stay in the filing for months after they pay off.</caption>
              <thead><tr>
                <th>Period</th><th>Pool balance</th><th>Active / reported</th>
                <th>WA rate</th><th>WA score</th><th>WA term (mo)</th>
                <th>30+ DQ</th><th>60+ DQ</th><th>90+ DQ</th>
                <th>Payoff</th><th>Net loss</th><th>Cum. loss</th>
              </tr></thead>
              <tbody>{rows}</tbody>
            </table></div>
            {charts}
            """;
    }

    private static string ComparisonSection(ReportData data)
    {
        if (data.Trusts.Count < 2)
        {
            return string.Empty;
        }

        var labels = data.Trusts
            .SelectMany(t => t.Periods)
            .Select(p => p.ReportingPeriodEnd)
            .Distinct()
            .Order()
            .Select(d => d.ToString("MMM yy", CultureInfo.InvariantCulture))
            .ToArray();

        ChartSeries Series(TrustReport trust, int index, Func<PoolPeriodMetrics, decimal?> select) =>
            new(trust.CreditTier is { Length: > 0 } tier ? tier : trust.Cik,
                Palette[index % Palette.Length],
                [.. trust.Periods.Select((p, i) => (labels.ElementAtOrDefault(i) ?? string.Empty, select(p)))]);

        var spread = SvgLineChart.Render(
            "cmp-spread", "Indicative spread over the nearest Treasury tenor", "%",
            [.. data.Trusts.Select((t, i) => Series(t, i, p => p.IndicativeSpreadPercent))]);

        var loss = SvgLineChart.Render(
            "cmp-loss", "Cumulative net loss over the periods held", "%",
            [.. data.Trusts.Select((t, i) => Series(t, i, p => p.CumulativeNetLossPercent))]);

        var dq = SvgLineChart.Render(
            "cmp-dq", "30+ day delinquency by balance", "%",
            [.. data.Trusts.Select((t, i) => Series(t, i, p => p.Dq30PlusPercent))]);

        var rows = new StringBuilder();
        foreach (var trust in data.Trusts)
        {
            if (trust.Latest is not { } p)
            {
                continue;
            }

            rows.Append(CultureInfo.InvariantCulture, $"""
                <tr>
                  <td>{Escape(trust.Name)}<span class="tier">{Escape(trust.CreditTier)}</span></td>
                  <td>{Pct(p.WaInterestRatePercent)}</td>
                  <td>{Num(p.WaRemainingTermMonths, 1)}</td>
                  <td>{Escape(p.TreasuryTenor ?? "-")}</td>
                  <td>{Pct(p.TreasuryRatePercent)}</td>
                  <td>{Pct(p.IndicativeSpreadPercent)}</td>
                </tr>
                """);
        }

        return $"""
            <h2>Across trusts</h2>
            <div class="note">
              <strong>The spread below is indicative and simplified.</strong>
              It is the pool's gross weighted-average loan rate less the Treasury par yield at
              the tenor nearest the pool's weighted-average remaining term. It is not a
              bond-equivalent spread, a discount margin or a Z-spread, and it is not any
              tranche's yield. It ignores the capital structure, servicing and excess spread,
              credit enhancement, the curve between tenors, and expected losses and
              prepayment &mdash; which is most of why a deep-subprime pool yields what it does.
            </div>
            <div class="scroller"><table>
              <caption>Indicative spread, most recent period. Both legs are percentages.</caption>
              <thead><tr>
                <th>Trust</th><th>WA pool rate</th><th>WA term (mo)</th>
                <th>Nearest tenor</th><th>Treasury yield</th><th>Indicative spread</th>
              </tr></thead>
              <tbody>{rows}</tbody>
            </table></div>
            {spread}{dq}{loss}
            """;
    }

    private static string MethodologySection() => """
        <h2>Method and caveats</h2>
        <p>
          Every formula, with the exact ABS-EE and Treasury field names it reads, is written out
          in <code>docs/metrics.md</code>. Four points matter for reading the figures above.
        </p>
        <h3>Rate conventions differ between the two sources</h3>
        <p>
          ABS-EE loan rates are decimal fractions despite the element being named
          <code>originalInterestRatePercentage</code>: <code>0.0639</code> means 6.39% APR.
          Treasury par yields are percentages: <code>4.19</code> means 4.19%. Everything on this
          page is a percentage; the spread subtracts like for like.
        </p>
        <h3>Only loans with a balance are in the pool</h3>
        <p>
          A paid-off loan keeps being reported for months. Averages and delinquency rates here
          cover loans with a positive ending balance only. The effect is not marginal: in the
          most seasoned pool roughly half the reported loans are already closed.
        </p>
        <h3>Delinquency is measured in days, over those that report it</h3>
        <p>
          <code>currentDelinquencyStatus</code> is days past due, not a bucket code, so 30+/60+/90+
          are day thresholds and the buckets are nested. The element is optional and genuinely
          absent on part of some filings; those loans are excluded from the denominator rather
          than assumed current, and the per-trust tables show how many.
        </p>
        <h3>Cumulative loss covers the periods held, not pool inception</h3>
        <p>
          The monthly filings do not state the pool's balance at issuance, and this report holds
          only the periods it has ingested. Cumulative net loss is therefore a running total over
          those periods over the summed original amount of the loans those filings report. It is
          not a since-inception loss figure and should not be read as one.
        </p>
        """;

    private static string ProvenanceSection(ReportData data)
    {
        var rows = new StringBuilder();
        foreach (var run in data.Provenance)
        {
            rows.Append(CultureInfo.InvariantCulture, $"""
                <tr>
                  <td>{run.RunId}</td>
                  <td>{Escape(run.Provider)}</td>
                  <td>{Escape(run.SubjectId ?? "-")}</td>
                  <td>{Fmt(run.ReportingPeriod)}</td>
                  <td>{run.RecordsLoaded:N0}</td>
                  <td>{Escape(run.CompletedAt?.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) ?? "-")}</td>
                  <td><code>{Escape(run.Sha256 is { Length: >= 12 } h ? h[..12] : "-")}</code></td>
                </tr>
                """);
        }

        return $"""
            <h2>Provenance</h2>
            <p>
              Every figure above traces to one of these ingestion runs. The hash is the SHA-256 of
              the raw file as filed, which is also what makes a re-run idempotent.
            </p>
            <div class="scroller"><table>
              <caption>Successful ingestion runs behind this report.</caption>
              <thead><tr>
                <th>Run</th><th>Provider</th><th>Subject</th><th>Period</th>
                <th>Records</th><th>Completed</th><th>Source SHA-256</th>
              </tr></thead>
              <tbody>{rows}</tbody>
            </table></div>
            <p class="foot">
              Generated by <code>fixed-income-data-acquisition</code>. Source data: SEC EDGAR Form
              ABS-EE exhibit EX-102 and the U.S. Treasury daily par yield curve, both public.
            </p>
            """;
    }

    private static string Fmt(DateOnly? date) =>
        date is { } d && d != default ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "-";

    private static string Money(decimal value) =>
        Math.Abs(value) >= 1_000_000m
            ? string.Create(CultureInfo.InvariantCulture, $"${value / 1_000_000m:N1}M")
            : string.Create(CultureInfo.InvariantCulture, $"${value:N0}");

    private static string Pct(decimal? value, int decimals = 2) =>
        value is { } v ? v.ToString($"N{decimals}", CultureInfo.InvariantCulture) + "%" : "-";

    private static string Num(decimal? value, int decimals) =>
        value is { } v ? v.ToString($"N{decimals}", CultureInfo.InvariantCulture) : "-";

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
