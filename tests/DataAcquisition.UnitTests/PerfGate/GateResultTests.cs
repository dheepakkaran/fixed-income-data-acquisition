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
    public void A_time_regression_beyond_the_time_threshold_fails()
    {
        // 1000 -> 2000 ns is +100%, past a 50% time threshold.
        var result = GateResult.Compare(
            ResultsOf(("A", 2000, 100)), BaselineOf(("A", 1000, 100)), 15) with { TimeThreshold = 50 };

        Assert.False(result.Passed);
        var regression = Assert.Single(result.Regressions);
        Assert.Equal(100, regression.TimeChangePercent);
        Assert.Equal("time", regression.RegressionKind(50, 15));
    }

    [Fact]
    public void A_time_change_within_hardware_variance_passes()
    {
        // +33% is what the same code measured moving from an Intel Xeon 8370C to an AMD EPYC
        // 7763 on GitHub's runner pool, with allocation identical. Failing there would make
        // every release a coin toss on which CPU the runner drew.
        var result = GateResult.Compare(
            ResultsOf(("A", 1330, 100)), BaselineOf(("A", 1000, 100)), 15) with { TimeThreshold = 50 };

        Assert.True(result.Passed);
    }

    [Fact]
    public void A_memory_change_inside_the_threshold_passes()
    {
        var result = GateResult.Compare(
            ResultsOf(("A", 1000, 114)), BaselineOf(("A", 1000, 100)), 15) with { TimeThreshold = 50 };

        Assert.True(result.Passed);
    }

    [Fact]
    public void A_memory_regression_alone_fails_on_the_tighter_threshold()
    {
        // Allocated bytes are deterministic and do not vary by machine, so 15% is meaningful
        // there even though it would be noise on time.
        var result = GateResult.Compare(
            ResultsOf(("A", 1000, 150)), BaselineOf(("A", 1000, 100)), 15) with { TimeThreshold = 50 };

        Assert.False(result.Passed);
        var regression = Assert.Single(result.Regressions);
        Assert.Equal(50, regression.MemoryChangePercent);
        Assert.Equal("memory", regression.RegressionKind(50, 15));
    }

    [Fact]
    public void The_deliberate_regression_trips_both_measures()
    {
        // The regex-per-call regression measured +203% time and +206% memory. Either threshold
        // alone would have caught it, which is what keeps the wider time tolerance honest.
        var result = GateResult.Compare(
            ResultsOf(("A", 3030, 306)), BaselineOf(("A", 1000, 100)), 15) with { TimeThreshold = 50 };

        Assert.False(result.Passed);
        Assert.Equal("time and memory", Assert.Single(result.Regressions).RegressionKind(50, 15));
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
        Assert.Contains("passed (enforcing, memory 15%, time 50%)", pass.ToMarkdown(enforcing: true), StringComparison.Ordinal);
        Assert.Contains("report-only", pass.ToMarkdown(enforcing: false), StringComparison.Ordinal);

        var fail = GateResult.Compare(ResultsOf(("A", 5000, 100)), BaselineOf(("A", 1000, 100)), 15);
        var markdown = fail.ToMarkdown(enforcing: false);

        Assert.Contains("found a regression", markdown, StringComparison.Ordinal);
        Assert.Contains("does not block the merge", markdown, StringComparison.Ordinal);
        Assert.Contains("⚠️", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cross_machine_comparison_is_flagged_rather_than_read_as_a_verdict()
    {
        // The failure this catches actually happened: a baseline from an Apple M5 compared
        // against an ubuntu-latest runner reported all eight benchmarks as regressed,
        // including one measured at -5.1% on the same baseline locally.
        var result = GateResult.Compare(
            ResultsOf(("A", 5000, 100)), BaselineOf(("A", 1000, 100)), 15) with
        {
            BaselineHost = "Linux Ubuntu 24.04 | Intel Xeon Platinum 8370C | X64",
            CurrentHost = "macOS 27.0 | Apple M5 | Arm64",
        };

        Assert.True(result.HostMismatch);

        var markdown = result.ToMarkdown(enforcing: true);
        Assert.Contains("different machines", markdown, StringComparison.Ordinal);
        Assert.Contains("Apple M5", markdown, StringComparison.Ordinal);
        Assert.Contains("Intel Xeon", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_machine_is_not_flagged()
    {
        var result = GateResult.Compare(
            ResultsOf(("A", 1000, 100)), BaselineOf(("A", 1000, 100)), 15) with
        {
            BaselineHost = "Linux Ubuntu 24.04 | Intel Xeon Platinum 8370C | X64",
            CurrentHost = "Linux Ubuntu 24.04 | Intel Xeon Platinum 8370C | X64",
        };

        Assert.False(result.HostMismatch);
        Assert.DoesNotContain("different machines", result.ToMarkdown(enforcing: true), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_host_on_either_side_is_not_treated_as_a_mismatch()
    {
        // An older baseline predates host recording; that is not evidence of a mismatch.
        var result = GateResult.Compare(
            ResultsOf(("A", 1000, 100)), BaselineOf(("A", 1000, 100)), 15) with
        {
            BaselineHost = null,
            CurrentHost = "macOS 27.0 | Apple M5 | Arm64",
        };

        Assert.False(result.HostMismatch);
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
            ["A"] = new(3000, null),
        };

        var result = GateResult.Compare(current, BaselineOf(("A", 1000, 100)), 15) with { TimeThreshold = 50 };

        Assert.False(result.Passed);
        Assert.Null(Assert.Single(result.Comparisons).MemoryChangePercent);
    }
}
