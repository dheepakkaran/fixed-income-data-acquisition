using DataAcquisition.Reports.Rendering;

namespace DataAcquisition.UnitTests.Reports;

public sealed class SvgLineChartTests
{
    private static IReadOnlyList<(string, decimal?)> Points(params decimal?[] values) =>
        [.. values.Select((v, i) => ($"P{i}", v))];

    [Fact]
    public void A_series_renders_a_polyline_and_markers()
    {
        var svg = SvgLineChart.Render(
            "c1", "Test", "%", [new ChartSeries("A", "#000", Points(1m, 2m, 3m))]);

        Assert.Contains("<polyline", svg, StringComparison.Ordinal);
        Assert.Contains("<circle", svg, StringComparison.Ordinal);
        Assert.Contains("viewBox=\"0 0 720 260\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gap_breaks_the_line_rather_than_being_drawn_as_zero()
    {
        var svg = SvgLineChart.Render(
            "c2", "Gap", "%", [new ChartSeries("A", "#000", Points(1m, null, 3m))]);

        // Two one-point segments cannot form a polyline, so the gap leaves markers only.
        Assert.DoesNotContain("<polyline", svg, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            svg, "<circle", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)).Count);
    }

    [Fact]
    public void A_series_with_no_values_says_so_instead_of_drawing_an_empty_axis()
    {
        var svg = SvgLineChart.Render(
            "c3", "Empty", "%", [new ChartSeries("A", "#000", Points(null, null))]);

        Assert.Contains("No data to chart", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<svg", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flat_series_still_gets_a_visible_band()
    {
        // All-equal values would otherwise divide by a zero range.
        var svg = SvgLineChart.Render(
            "c4", "Flat", "%", [new ChartSeries("A", "#000", Points(5m, 5m, 5m))]);

        Assert.Contains("<polyline", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_point_is_centred_and_does_not_divide_by_zero()
    {
        var svg = SvgLineChart.Render(
            "c5", "One", "%", [new ChartSeries("A", "#000", Points(42m))]);

        Assert.Contains("<circle", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_series_get_a_legend()
    {
        var svg = SvgLineChart.Render("c6", "Two", "%",
        [
            new ChartSeries("First", "#111", Points(1m, 2m)),
            new ChartSeries("Second", "#222", Points(3m, 4m)),
        ]);

        Assert.Contains("class=\"legend\"", svg, StringComparison.Ordinal);
        Assert.Contains("First", svg, StringComparison.Ordinal);
        Assert.Contains("Second", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Titles_are_escaped()
    {
        var svg = SvgLineChart.Render(
            "c7", "A & B <tag>", "%", [new ChartSeries("A", "#000", Points(1m, 2m))]);

        Assert.Contains("A &amp; B &lt;tag&gt;", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<tag>", svg, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(0.5, 3.2)]
    [InlineData(-2, 7)]
    [InlineData(5, 5)]
    public void Axis_bounds_always_enclose_the_data(double min, double max)
    {
        var (lower, upper) = SvgLineChart.NiceBounds((decimal)min, (decimal)max);

        Assert.True(lower <= (decimal)min, $"lower {lower} should be <= {min}");
        Assert.True(upper >= (decimal)max, $"upper {upper} should be >= {max}");
        Assert.True(upper > lower);
    }
}
