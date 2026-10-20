using PerfGate;
using PerfGate.Model;

namespace DataAcquisition.UnitTests.PerfGate;

/// <summary>
/// The gate's comparison logic. Worth testing directly: a gate that mis-reports is worse
/// than no gate, because a green tick is taken as evidence.
/// </summary>
public sealed class GateResultTests
{
    private static Baseline BaselineOf(params (string Name, double MeanNs, long Bytes)[] entries) =>
        new()
        {
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Benchmarks = entries.ToDictionary(
                e => e.Name,
                e => new BenchmarkMeasurement(e.MeanNs, e.Bytes),
                StringComparer.Ordinal),
        };

    private static Dictionary<string, BenchmarkMeasurement> ResultsOf(
        params (string Name, double MeanNs, long Bytes)[] entries) =>
        entries.ToDictionary(
            e => e.Name,
            e => new BenchmarkMeasurement(e.MeanNs, e.Bytes),
            StringComparer.Ordinal);

    [Fact]
    public void An_unchanged_run_passes()
    {
        var result = GateResult.Compare(
            ResultsOf(("A", 1000, 100)), BaselineOf(("A", 1000, 100)), 15);

        Assert.True(result.Passed);
        Assert.Equal(0, Assert.Single(result.Comparisons).TimeChangePercent);
    }

    [Fact]
    public void A_regression_beyond_the_threshold_fails()
    {
        // 1000 -> 1200 ns is +20%, past a 15% threshold.
        var result = GateResult.Compare(
            ResultsOf(("A", 1200, 100)), BaselineOf(("A", 1000, 100)), 15);

        Assert.False(result.Passed);
        var regression = Assert.Single(result.Regressions);
        Assert.Equal(20, regression.TimeChangePercent);
    }

    [Fact]
    public void A_change_inside_the_threshold_passes()
    {
        // +14% is noise on a short job on a shared runner, which is what the threshold absorbs.
        var result = GateResult.Compare(
            ResultsOf(("A", 1140, 100)), BaselineOf(("A", 1000, 100)), 15);

        Assert.True(result.Passed);
    }

    [Fact]
    public void A_memory_regression_alone_fails()
    {
        // Time unchanged, allocation up 50%: still a regression.
        var result = GateResult.Compare(
            ResultsOf(("A", 1000, 150)), BaselineOf(("A", 1000, 100)), 15);

        Assert.False(result.Passed);
        Assert.Equal(50, Assert.Single(result.Regressions).MemoryChangePercent);
    }

    [Fact]
    public void An_improvement_is_never_a_regression()
    {
        var result = GateResult.Compare(
            ResultsOf(("A", 500, 50)), BaselineOf(("A", 1000, 100)), 15);

        Assert.True(result.Passed);
        Assert.Equal(-50, Assert.Single(result.Comparisons).TimeChangePercent);
    }

    [Fact]
    public void A_new_benchmark_is_reported_but_does_not_fail()
    {
        // Nothing to compare against, so calling it a regression would be an invention.
        var result = GateResult.Compare(
            ResultsOf(("A", 1000, 100), ("B", 9999, 999)), BaselineOf(("A", 1000, 100)), 15);

        Assert.True(result.Passed);
        Assert.Contains(result.Comparisons, c => c.Name == "B" && c.IsNew);
        Assert.Contains("new", result.ToMarkdown(enforcing: true), StringComparison.Ordinal);
    }

    [Fact]
    public void A_baseline_entry_with_no_result_is_reported_not_failed()
    {
        // Usually a rename or a filtered suite, which the gate cannot distinguish from a
        // deleted benchmark, so it reports rather than guesses.
        var result = GateResult.Compare(
            ResultsOf(("A", 1000, 100)), BaselineOf(("A", 1000, 100), ("Gone", 1, 1)), 15);

        Assert.True(result.Passed);
        Assert.Equal("Gone", Assert.Single(result.MissingFromResults));
        Assert.Contains("renamed or filtered out", result.ToMarkdown(enforcing: true), StringComparison.Ordinal);
    }

    [Fact]
    public void The_markdown_states_the_mode_and_threshold()
    {
        var pass = GateResult.Compare(ResultsOf(("A", 1000, 100)), BaselineOf(("A", 1000, 100)), 15);
        Assert.Contains("passed (enforcing, threshold 15%)", pass.ToMarkdown(enforcing: true), StringComparison.Ordinal);
        Assert.Contains("report-only", pass.ToMarkdown(enforcing: false), StringComparison.Ordinal);

        var fail = GateResult.Compare(ResultsOf(("A", 2000, 100)), BaselineOf(("A", 1000, 100)), 15);
        var markdown = fail.ToMarkdown(enforcing: false);

        Assert.Contains("found a regression", markdown, StringComparison.Ordinal);
        Assert.Contains("does not block the merge", markdown, StringComparison.Ordinal);
        Assert.Contains("⚠️", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zero_baseline_does_not_divide_by_zero()
    {
        var result = GateResult.Compare(
            ResultsOf(("A", 1000, 100)), BaselineOf(("A", 0, 0)), 15);

        Assert.True(result.Passed);
        var comparison = Assert.Single(result.Comparisons);
        Assert.Null(comparison.TimeChangePercent);
        Assert.Null(comparison.MemoryChangePercent);
        Assert.DoesNotContain("NaN", result.ToMarkdown(enforcing: true), StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", result.ToMarkdown(enforcing: true), StringComparison.Ordinal);
    }

    [Fact]
    public void Results_without_a_memory_measurement_still_compare_on_time()
    {
        var current = new Dictionary<string, BenchmarkMeasurement>(StringComparer.Ordinal)
        {
            ["A"] = new(1300, null),
        };

        var result = GateResult.Compare(current, BaselineOf(("A", 1000, 100)), 15);

        Assert.False(result.Passed);
        Assert.Null(Assert.Single(result.Comparisons).MemoryChangePercent);
    }
}
