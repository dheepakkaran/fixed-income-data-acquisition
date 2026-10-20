using System.Globalization;
using System.Text;
using PerfGate.Model;

namespace PerfGate;

/// <summary>How one benchmark changed against the baseline.</summary>
/// <param name="Name">Benchmark key.</param>
/// <param name="BaselineMeanNs">Baseline mean, or null when the benchmark is new.</param>
/// <param name="CurrentMeanNs">Measured mean.</param>
/// <param name="BaselineBytes">Baseline allocation, or null.</param>
/// <param name="CurrentBytes">Measured allocation, or null.</param>
public sealed record BenchmarkComparison(
    string Name,
    double? BaselineMeanNs,
    double CurrentMeanNs,
    long? BaselineBytes,
    long? CurrentBytes)
{
    /// <summary>Percentage change in mean time. Positive is slower.</summary>
    public double? TimeChangePercent => BaselineMeanNs is > 0
        ? (CurrentMeanNs - BaselineMeanNs.Value) / BaselineMeanNs.Value * 100
        : null;

    /// <summary>Percentage change in allocation. Positive is more memory.</summary>
    public double? MemoryChangePercent => BaselineBytes is > 0 && CurrentBytes is not null
        ? (CurrentBytes.Value - BaselineBytes.Value) / (double)BaselineBytes.Value * 100
        : null;

    /// <summary>True when this benchmark has no baseline to compare against.</summary>
    public bool IsNew => BaselineMeanNs is null;

    public bool RegressedOn(double threshold) =>
        TimeChangePercent > threshold || MemoryChangePercent > threshold;
}

/// <summary>The gate's verdict over every benchmark.</summary>
/// <param name="Threshold">Percentage regression tolerated.</param>
/// <param name="Comparisons">One entry per benchmark measured.</param>
/// <param name="MissingFromResults">Baseline entries the run did not produce.</param>
public sealed record GateResult(
    double Threshold,
    IReadOnlyList<BenchmarkComparison> Comparisons,
    IReadOnlyList<string> MissingFromResults)
{
    public IReadOnlyList<BenchmarkComparison> Regressions =>
        [.. Comparisons.Where(c => c.RegressedOn(Threshold))];

    public bool Passed => Regressions.Count == 0;

    /// <summary>Compare a run against a baseline.</summary>
    public static GateResult Compare(
        Dictionary<string, BenchmarkMeasurement> current,
        Baseline baseline,
        double threshold)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(baseline);

        var comparisons = current
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                baseline.Benchmarks.TryGetValue(kv.Key, out var previous);
                return new BenchmarkComparison(
                    kv.Key,
                    previous?.MeanNs,
                    kv.Value.MeanNs,
                    previous?.AllocatedBytes,
                    kv.Value.AllocatedBytes);
            })
            .ToArray();

        // A baseline entry with no result usually means a benchmark was renamed or the suite
        // was filtered. Reported rather than failed, because the gate cannot tell which.
        var missing = baseline.Benchmarks.Keys
            .Where(k => !current.ContainsKey(k))
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new GateResult(threshold, comparisons, missing);
    }

    /// <summary>A markdown table for the job summary and the PR comment.</summary>
    public string ToMarkdown(bool enforcing)
    {
        var md = new StringBuilder();

        var verdict = Passed ? "passed" : "found a regression";
        var mode = enforcing ? "enforcing" : "report-only";
        md.Append(CultureInfo.InvariantCulture,
            $"## Performance gate {verdict} ({mode}, threshold {Threshold:N0}%)\n\n");

        if (!Passed && !enforcing)
        {
            md.Append(
                "> Report-only on pull requests into `develop`, so this does not block the merge. "
                + "It is enforcing on release pull requests into `main`.\n\n");
        }

        md.Append("| Benchmark | Mean | vs baseline | Allocated | vs baseline |\n");
        md.Append("|---|---:|---:|---:|---:|\n");

        foreach (var c in Comparisons)
        {
            var flag = c.RegressedOn(Threshold) ? " ⚠️" : string.Empty;
            md.Append(CultureInfo.InvariantCulture,
                $"| `{c.Name}`{flag} | {FormatTime(c.CurrentMeanNs)} | {FormatChange(c.TimeChangePercent, c.IsNew)} "
                + $"| {FormatBytes(c.CurrentBytes)} | {FormatChange(c.MemoryChangePercent, c.IsNew)} |\n");
        }

        if (MissingFromResults.Count > 0)
        {
            md.Append(CultureInfo.InvariantCulture,
                $"\nIn the baseline but not in this run (renamed or filtered out): {string.Join(", ", MissingFromResults.Select(m => $"`{m}`"))}.\n");
        }

        if (!Passed)
        {
            md.Append("\n**Regressions**\n\n");
            foreach (var c in Regressions)
            {
                md.Append(CultureInfo.InvariantCulture,
                    $"- `{c.Name}`: {FormatTime(c.BaselineMeanNs ?? 0)} to {FormatTime(c.CurrentMeanNs)}"
                    + $" ({FormatChange(c.TimeChangePercent, false)} time, {FormatChange(c.MemoryChangePercent, false)} memory)\n");
            }
        }

        return md.ToString();
    }

    private static string FormatTime(double nanoseconds) => nanoseconds switch
    {
        >= 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{nanoseconds / 1_000_000_000:N2} s"),
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{nanoseconds / 1_000_000:N2} ms"),
        >= 1_000 => string.Create(CultureInfo.InvariantCulture, $"{nanoseconds / 1_000:N2} us"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{nanoseconds:N1} ns"),
    };

    private static string FormatBytes(long? bytes) => bytes switch
    {
        null => "-",
        >= 1_048_576 => string.Create(CultureInfo.InvariantCulture, $"{bytes.Value / 1_048_576.0:N2} MB"),
        >= 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes.Value / 1024.0:N1} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes.Value:N0} B"),
    };

    private static string FormatChange(double? percent, bool isNew) => isNew
        ? "new"
        : percent is { } p
            ? string.Create(CultureInfo.InvariantCulture, $"{(p >= 0 ? "+" : string.Empty)}{p:N1}%")
            : "-";
}
