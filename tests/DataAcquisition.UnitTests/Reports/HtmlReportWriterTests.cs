using System.Text.RegularExpressions;
using DataAcquisition.Reports.Model;
using DataAcquisition.Reports.Rendering;

namespace DataAcquisition.UnitTests.Reports;

/// <summary>
/// Structural checks on the rendered page. A report that silently publishes <c>NaN</c>, an
/// empty cell or a missing section is worse than one that fails to build, because nobody
/// notices.
/// </summary>
public sealed class HtmlReportWriterTests
{
    private static PoolPeriodMetrics Period(int month, decimal balance = 1_000_000m) => new()
    {
        TrustCik = "2099048",
        ReportingPeriodEnd = new DateOnly(2026, month, DateTime.DaysInMonth(2026, month)),
        LoansReported = 29_152,
        LoansActive = 28_627,
        PoolBalance = balance,
        OriginalPoolBalance = 1_337_700_000m,
        WaInterestRatePercent = 6.73m,
        WaCreditScore = 770.4m,
        WaRemainingTermMonths = 55.9m,
        Dq30PlusPercent = 0.97m,
        Dq60PlusPercent = 0.28m,
        Dq90PlusPercent = 0.05m,
        ActiveWithoutDelinquencyStatus = 546,
        LoansPaidOff = 431,
        PayoffRatePercent = 1.51m,
        LoansChargedOff = 63,
        ChargedOffPrincipal = 613_396m,
        Recovered = 461_070m,
        NetLoss = 152_326m,
        CumulativeNetLoss = 1_036_112m,
        CumulativeNetLossPercent = 0.080m,
        TreasuryQuoteDate = new DateOnly(2026, month, 28),
        TreasuryTenor = "5Y",
        TreasuryRatePercent = 4.49m,
        IndicativeSpreadPercent = 2.24m,
    };

    private static ReportData Data(int trustCount = 2, int periodCount = 3)
    {
        var trusts = Enumerable.Range(0, trustCount).Select(i => new TrustReport(
            $"20990{i}",
            $"Test Trust {i}",
            i == 0 ? "Prime" : "Deep subprime",
            [.. Enumerable.Range(6, periodCount).Select(m => Period(m, 1_000_000m * (periodCount - m + 6)))]))
            .ToArray();

        return new ReportData(
            new DateTimeOffset(2026, 10, 20, 7, 0, 0, TimeSpan.Zero),
            trusts,
            [
                new RunProvenance
                {
                    RunId = 1, Provider = "SecAbsEe", SubjectId = "2099048",
                    ReportingPeriod = new DateOnly(2026, 8, 31),
                    CompletedAt = new DateTimeOffset(2026, 10, 20, 6, 47, 0, TimeSpan.Zero),
                    RecordsLoaded = 29_152, Sha256 = "abcdef0123456789",
                },
            ],
            new DateOnly(2026, 10, 19));
    }

    [Fact]
    public void Every_section_is_present()
    {
        var html = HtmlReportWriter.Render(Data());

        Assert.Contains("ABS Pool Performance Report", html, StringComparison.Ordinal);
        Assert.Contains("Data freshness", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Summary</h2>", html, StringComparison.Ordinal);
        Assert.Contains("Across trusts", html, StringComparison.Ordinal);
        Assert.Contains("Method and caveats", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Provenance</h2>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void No_section_renders_a_nan_or_an_infinity()
    {
        var html = HtmlReportWriter.Render(Data());

        Assert.DoesNotContain("NaN", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", html, StringComparison.Ordinal);
        Assert.DoesNotContain("undefined", html, StringComparison.Ordinal);
        Assert.DoesNotContain("null", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_table_cell_is_blank()
    {
        var html = HtmlReportWriter.Render(Data());

        // An absent value renders as "-", never as an empty cell, so a reader can tell
        // "no data" from "zero".
        Assert.DoesNotContain("<td></td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<td> </td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<th></th>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_carries_no_javascript()
    {
        var html = HtmlReportWriter.Render(Data());

        // Static page by design: GitHub Pages serves it as a file and there is nothing to
        // interact with.
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Charts_are_inline_svg_with_accessible_labels()
    {
        var html = HtmlReportWriter.Render(Data());

        var charts = Regex.Matches(html, "<svg", RegexOptions.None, TimeSpan.FromSeconds(2)).Count;

        // Two per trust plus three comparison charts.
        Assert.Equal(7, charts);
        Assert.Contains("role=\"img\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-labelledby", html, StringComparison.Ordinal);
        Assert.Contains("<title id=", html, StringComparison.Ordinal);
        Assert.Contains("<desc id=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_spread_is_labelled_indicative_wherever_it_appears()
    {
        var html = HtmlReportWriter.Render(Data());

        Assert.Contains("indicative and simplified", html, StringComparison.OrdinalIgnoreCase);
        // Matched on the distinctive phrases alone: the prose wraps across lines in the
        // source, so a longer substring would be testing the line breaks.
        Assert.Contains("bond-equivalent spread", html, StringComparison.Ordinal);
        Assert.Contains("discount margin", html, StringComparison.Ordinal);
        Assert.Contains("Z-spread", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Provenance_cites_run_ids_and_source_hashes()
    {
        var html = HtmlReportWriter.Render(Data());

        Assert.Contains("Source SHA-256", html, StringComparison.Ordinal);
        Assert.Contains("abcdef012345", html, StringComparison.Ordinal);
        Assert.Contains("29,152", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Absent_values_render_as_a_dash_rather_than_disappearing()
    {
        var sparse = Period(8) with
        {
            WaCreditScore = null,
            Dq30PlusPercent = null,
            IndicativeSpreadPercent = null,
            TreasuryTenor = null,
        };

        var data = Data() with
        {
            Trusts = [new TrustReport("2099048", "Sparse Trust", "Prime", [sparse])],
        };

        var html = HtmlReportWriter.Render(data);

        Assert.Contains("<td>-</td>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<td></td>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_trust_omits_the_comparison_section()
    {
        var html = HtmlReportWriter.Render(Data(trustCount: 1));

        // Nothing to compare against, so the section would be an empty promise.
        Assert.DoesNotContain("Across trusts", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Summary</h2>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_period_still_renders()
    {
        // Charts with one point cannot draw a line; markers still show and nothing throws.
        var html = HtmlReportWriter.Render(Data(periodCount: 1));

        Assert.Contains("<svg", html, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_is_escaped_so_a_trust_name_cannot_inject_markup()
    {
        var data = Data() with
        {
            Trusts = [new TrustReport("1", "Acme <script>alert(1)</script> & Co", "Prime", [Period(8)])],
        };

        var html = HtmlReportWriter.Render(data);

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&amp; Co", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Wide_tables_are_wrapped_so_a_phone_scrolls_instead_of_breaking()
    {
        var html = HtmlReportWriter.Render(Data());

        var openers = Regex.Matches(html, "<div class=\"scroller\">", RegexOptions.None, TimeSpan.FromSeconds(2)).Count;
        var closers = Regex.Matches(html, "</table></div>", RegexOptions.None, TimeSpan.FromSeconds(2)).Count;

        Assert.True(openers > 0);
        Assert.Equal(openers, closers);
    }
}
