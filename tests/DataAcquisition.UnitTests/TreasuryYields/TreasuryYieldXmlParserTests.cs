using DataAcquisition.Providers.TreasuryYields.Model;
using DataAcquisition.Providers.TreasuryYields.Parsing;

namespace DataAcquisition.UnitTests.TreasuryYields;

/// <summary>
/// Parser tests against a real trimmed slice of the Treasury feed. Expected values were read
/// out of the fixture, so they are Treasury's numbers and not the parser's.
/// </summary>
public sealed class TreasuryYieldXmlParserTests
{
    private static string Fixture => Path.Combine(
        AppContext.BaseDirectory, "fixtures", "treasury", "par-yields-2026-sample.xml");

    private static async Task<List<TreasuryParYield>> ParseAsync()
    {
        var points = new List<TreasuryParYield>();
        await foreach (var point in TreasuryYieldXmlParser.ParseFileAsync(Fixture))
        {
            points.Add(point);
        }

        return points;
    }

    [Fact]
    public async Task Every_tenor_of_every_day_is_parsed()
    {
        var points = await ParseAsync();

        // Five business days x fourteen tenors. An earlier version of the parser advanced
        // the reader after ReadElementContentAsString, which already advances, and so
        // captured only every other tenor: a full year came back as 1,400 points instead of
        // 2,800. This count is the guard against that returning.
        Assert.Equal(70, points.Count);
        Assert.Equal(5, points.Select(p => p.QuoteDate).Distinct().Count());
        Assert.Equal(14, points.Select(p => p.TenorMonths).Distinct().Count());
    }

    [Fact]
    public async Task The_duplicate_thirty_year_field_is_excluded()
    {
        // The feed publishes BC_30YEARDISPLAY alongside BC_30YEAR with the same value:
        // fifteen BC_ fields per day, fourteen real tenors. Including it would put two rows
        // at 360 months and double-count the long end of the curve.
        Assert.Equal(15, System.Text.RegularExpressions.Regex.Matches(
            File.ReadAllText(Fixture), "<d:BC_[A-Z0-9_]+ ").Count / 5);

        var points = await ParseAsync();

        Assert.Single(points, p => p.QuoteDate == new DateOnly(2026, 8, 31) && p.TenorMonths == 360m);
        Assert.DoesNotContain(
            TreasuryYieldXmlParser.DuplicateField,
            TreasuryYieldXmlParser.KnownTenors.Select(t => t.Field));
    }

    [Fact]
    public async Task Rates_match_the_published_curve()
    {
        var points = await ParseAsync();

        var curve = points
            .Where(p => p.QuoteDate == new DateOnly(2026, 6, 30))
            .ToDictionary(p => p.TenorLabel, p => p.RatePercent, StringComparer.Ordinal);

        // Percentages, which is how Treasury publishes them: 4.14 means 4.14%.
        Assert.Equal(4.14m, curve["2Y"]);
        Assert.Equal(4.15m, curve["3Y"]);
        Assert.Equal(4.19m, curve["5Y"]);
        Assert.Equal(4.44m, curve["10Y"]);
    }

    [Fact]
    public async Task Tenors_carry_months_so_the_curve_sorts_correctly()
    {
        var points = await ParseAsync();

        var ordered = points
            .Where(p => p.QuoteDate == new DateOnly(2026, 8, 31))
            .OrderBy(p => p.TenorMonths)
            .Select(p => p.TenorLabel)
            .ToArray();

        // Field names do not sort into curve order as text: "BC_1_5MONTH" would land next
        // to "BC_10YEAR". Months are the sort key for that reason.
        Assert.Equal(
            ["1M", "1.5M", "2M", "3M", "4M", "6M", "1Y", "2Y", "3Y", "5Y", "7Y", "10Y", "20Y", "30Y"],
            ordered);

        Assert.Equal(1.5m, points.First(p => p.TenorLabel == "1.5M").TenorMonths);
    }

    [Fact]
    public async Task The_feed_covers_the_abs_reporting_period_ends()
    {
        // The spread metric needs a curve as at each pool's period end.
        var dates = (await ParseAsync()).Select(p => p.QuoteDate).Distinct().ToHashSet();

        Assert.Contains(new DateOnly(2026, 6, 30), dates);
        Assert.Contains(new DateOnly(2026, 7, 31), dates);
        Assert.Contains(new DateOnly(2026, 8, 31), dates);
    }

    [Theory]
    [InlineData("2026-01-02T00:00:00", 2026, 1, 2)]
    [InlineData("2026-08-31T00:00:00", 2026, 8, 31)]
    public void Quote_dates_parse_from_the_edm_datetime_form(string value, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), TreasuryYieldXmlParser.ParseQuoteDate(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("N/A")]
    public void A_blank_tenor_is_null_rather_than_zero(string value)
    {
        // A day with no quote at a tenor is missing data, not a yield of zero. 31 of 2025's
        // 3,486 possible points are blank in the real feed.
        Assert.Null(TreasuryYieldXmlParser.ParseRate(value));
    }

    [Fact]
    public void Known_tenors_are_the_fourteen_real_points_on_the_curve()
    {
        Assert.Equal(14, TreasuryYieldXmlParser.KnownTenors.Count);
        Assert.Equal(
            [1m, 1.5m, 2m, 3m, 4m, 6m, 12m, 24m, 36m, 60m, 84m, 120m, 240m, 360m],
            TreasuryYieldXmlParser.KnownTenors.Select(t => t.Months));
    }
}
