using System.Globalization;
using System.Text;

namespace DataAcquisition.Reports.Rendering;

/// <summary>One plotted series.</summary>
/// <param name="Label">Legend text.</param>
/// <param name="Colour">Stroke colour.</param>
/// <param name="Points">X label and Y value per point; a null Y is a gap.</param>
public sealed record ChartSeries(string Label, string Colour, IReadOnlyList<(string X, decimal? Y)> Points);

/// <summary>
/// Renders an inline SVG line chart. Hand-drawn rather than taken from a charting library:
/// the report is a static page with no JavaScript, and axes plus a polyline is a small
/// enough job that a dependency would cost more than it saves.
/// </summary>
public static class SvgLineChart
{
    private const int Width = 720;
    private const int Height = 260;
    private const int Left = 64;
    private const int Right = 16;
    private const int Top = 16;
    private const int Bottom = 46;

    /// <summary>
    /// Draw a chart. Returns an accessible figure with a title and description rather than a
    /// bare <c>svg</c>, so the page is readable by a screen reader too.
    /// </summary>
    /// <param name="id">Unique id, used to associate the title and description.</param>
    /// <param name="title">Chart title.</param>
    /// <param name="unit">Appended to axis labels, for example <c>%</c> or <c>$M</c>.</param>
    /// <param name="series">Series to plot. Series with no usable points are skipped.</param>
    public static string Render(string id, string title, string unit, IReadOnlyList<ChartSeries> series)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(series);

        var usable = series.Where(s => s.Points.Any(p => p.Y is not null)).ToArray();
        if (usable.Length == 0)
        {
            return $"<p class=\"empty\">No data to chart for {Escape(title)}.</p>";
        }

        var values = usable.SelectMany(s => s.Points).Where(p => p.Y is not null).Select(p => p.Y!.Value).ToArray();
        var (min, max) = NiceBounds(values.Min(), values.Max());

        var labels = usable[0].Points.Select(p => p.X).ToArray();
        var plotWidth = Width - Left - Right;
        var plotHeight = Height - Top - Bottom;

        decimal XAt(int index) => labels.Length == 1
            ? Left + (plotWidth / 2m)
            : Left + (plotWidth * index / (decimal)(labels.Length - 1));

        decimal YAt(decimal value) => max == min
            ? Top + (plotHeight / 2m)
            : Top + plotHeight - (plotHeight * (value - min) / (max - min));

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture,
            $"""<figure class="chart"><svg viewBox="0 0 {Width} {Height}" role="img" aria-labelledby="{id}-t {id}-d" xmlns="http://www.w3.org/2000/svg">""");
        svg.Append(CultureInfo.InvariantCulture, $"""<title id="{id}-t">{Escape(title)}</title>""");
        svg.Append(CultureInfo.InvariantCulture,
            $"""<desc id="{id}-d">{Escape(Describe(title, unit, usable))}</desc>""");

        // Horizontal gridlines and y-axis labels.
        for (var step = 0; step <= 4; step++)
        {
            var value = min + ((max - min) * step / 4m);
            var y = YAt(value);
            svg.Append(CultureInfo.InvariantCulture,
                $"""<line class="grid" x1="{Left}" y1="{F(y)}" x2="{Width - Right}" y2="{F(y)}" />""");
            svg.Append(CultureInfo.InvariantCulture,
                $"""<text class="axis" x="{Left - 8}" y="{F(y + 4)}" text-anchor="end">{Escape(FormatValue(value) + unit)}</text>""");
        }

        // X-axis labels. Thinned when they would collide.
        var every = labels.Length <= 8 ? 1 : (int)Math.Ceiling(labels.Length / 8.0);
        for (var i = 0; i < labels.Length; i++)
        {
            if (i % every != 0 && i != labels.Length - 1)
            {
                continue;
            }

            svg.Append(CultureInfo.InvariantCulture,
                $"""<text class="axis" x="{F(XAt(i))}" y="{Height - Bottom + 20}" text-anchor="middle">{Escape(labels[i])}</text>""");
        }

        foreach (var line in usable)
        {
            // Null values break the line rather than being drawn as zero.
            var segments = new List<List<(decimal X, decimal Y)>>();
            var current = new List<(decimal X, decimal Y)>();

            for (var i = 0; i < line.Points.Count; i++)
            {
                if (line.Points[i].Y is { } value)
                {
                    current.Add((XAt(i), YAt(value)));
                }
                else if (current.Count > 0)
                {
                    segments.Add(current);
                    current = [];
                }
            }

            if (current.Count > 0)
            {
                segments.Add(current);
            }

            foreach (var segment in segments.Where(s => s.Count > 1))
            {
                var path = string.Join(" ", segment.Select(p =>
                    string.Create(CultureInfo.InvariantCulture, $"{F(p.X)},{F(p.Y)}")));
                svg.Append(CultureInfo.InvariantCulture,
                    $"""<polyline fill="none" stroke="{line.Colour}" stroke-width="2.5" stroke-linejoin="round" points="{path}" />""");
            }

            // Markers, so a two- or three-point series is still legible.
            foreach (var point in segments.SelectMany(s => s))
            {
                svg.Append(CultureInfo.InvariantCulture,
                    $"""<circle cx="{F(point.X)}" cy="{F(point.Y)}" r="3.5" fill="{line.Colour}" />""");
            }
        }

        svg.Append("</svg>");

        if (usable.Length > 1)
        {
            svg.Append("""<ul class="legend">""");
            foreach (var line in usable)
            {
                svg.Append(CultureInfo.InvariantCulture,
                    $"""<li><span class="swatch" style="background:{line.Colour}"></span>{Escape(line.Label)}</li>""");
            }

            svg.Append("</ul>");
        }

        svg.Append(CultureInfo.InvariantCulture, $"<figcaption>{Escape(title)}</figcaption></figure>");
        return svg.ToString();
    }

    /// <summary>A sentence stating the series and their ranges, for the SVG description.</summary>
    private static string Describe(string title, string unit, IReadOnlyList<ChartSeries> series)
    {
        var parts = series.Select(s =>
        {
            var ys = s.Points.Where(p => p.Y is not null).Select(p => p.Y!.Value).ToArray();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{s.Label} from {FormatValue(ys[0])}{unit} to {FormatValue(ys[^1])}{unit}");
        });

        return $"{title}: {string.Join("; ", parts)}.";
    }

    /// <summary>Round the axis outward to a readable step so labels are not arbitrary.</summary>
    internal static (decimal Min, decimal Max) NiceBounds(decimal min, decimal max)
    {
        if (min == max)
        {
            // A flat series still needs a visible band.
            var pad = min == 0 ? 1m : Math.Abs(min) * 0.1m;
            return (min - pad, max + pad);
        }

        var lower = Math.Min(0m, min);
        var range = max - lower;
        var step = NiceStep(range / 4m);

        return (Math.Floor(lower / step) * step, Math.Ceiling(max / step) * step);
    }

    private static decimal NiceStep(decimal rough)
    {
        if (rough <= 0)
        {
            return 1m;
        }

        var magnitude = (decimal)Math.Pow(10, Math.Floor(Math.Log10((double)rough)));
        var normalised = rough / magnitude;

        var nice = normalised switch
        {
            <= 1m => 1m,
            <= 2m => 2m,
            <= 5m => 5m,
            _ => 10m,
        };

        return nice * magnitude;
    }

    private static string FormatValue(decimal value) => value switch
    {
        0 => "0",
        >= 100 or <= -100 => value.ToString("N0", CultureInfo.InvariantCulture),
        >= 10 or <= -10 => value.ToString("0.#", CultureInfo.InvariantCulture),
        _ => value.ToString("0.##", CultureInfo.InvariantCulture),
    };

    private static string F(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal);
}
