using System.Globalization;
using System.Text.Json;
using PerfGate;
using PerfGate.Model;

// Compares a BenchmarkDotNet run against a recorded baseline and fails on a regression
// beyond a threshold. Run by the perf-gate CI job; see docs/decisions.md D17 for why it is
// enforcing on release pull requests and report-only on pull requests into develop.
//
//   PerfGate compare --results <dir> --baseline <file> [--threshold 15] [--report-only]
//                    [--summary <file>] [--comment <file>]
//   PerfGate capture --results <dir> --out <file> [--commit <sha>]

var options = ParseArgs(args);
var command = args.Length > 0 ? args[0] : "help";

return command switch
{
    "compare" => Compare(options),
    "capture" => Capture(options),
    _ => Usage(),
};

static int Compare(Dictionary<string, string> options)
{
    var resultsDirectory = options.GetValueOrDefault("results", "BenchmarkDotNet.Artifacts/results");
    var baselinePath = options.GetValueOrDefault("baseline", "benchmarks/baseline.json");
    var threshold = double.TryParse(
        options.GetValueOrDefault("threshold"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
        ? t
        : 15.0;

    var reportOnly = options.ContainsKey("report-only");

    BenchmarkRun run;
    try
    {
        run = BenchmarkResultReader.ReadRun(resultsDirectory);
    }
    catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }

    var current = run.Measurements;

    if (!File.Exists(baselinePath))
    {
        // No baseline is not a regression. It happens on the first run and whenever the file
        // has not been captured yet, and failing there would block a pull request over a
        // missing file rather than a slow change.
        Console.WriteLine(
            $"No baseline at '{baselinePath}'. Nothing to compare against; recording {current.Count} benchmark(s) as the starting point.");
        Write(options, $"## Performance gate\n\nNo baseline at `{baselinePath}`, so there is nothing to compare "
            + $"against yet. This run measured {current.Count} benchmark(s).\n", reportOnly);
        return 0;
    }

    var baseline = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(baselinePath))
        ?? new Baseline();

    var result = GateResult.Compare(current, baseline, threshold) with
    {
        BaselineHost = baseline.Runtime,
        CurrentHost = run.Host,
    };

    var markdown = result.ToMarkdown(enforcing: !reportOnly);

    if (result.HostMismatch)
    {
        // Loud, but not fatal. The percentages are meaningless rather than bad, and failing
        // here would block a release on a stale baseline instead of on a slow change.
        Console.Error.WriteLine(
            "::warning::The baseline and this run came from different machines, so the "
            + $"percentages are not meaningful. Baseline: '{result.BaselineHost}'. "
            + $"This run: '{result.CurrentHost}'.");
    }

    Console.WriteLine(markdown);
    Write(options, markdown, reportOnly);

    if (result.Passed)
    {
        Console.WriteLine($"Performance gate passed: no benchmark regressed by more than {threshold:N0}%.");
        return 0;
    }

    foreach (var regression in result.Regressions)
    {
        Console.Error.WriteLine(
            $"::warning::{regression.Name} regressed "
            + $"{regression.TimeChangePercent:N1}% in time, {regression.MemoryChangePercent:N1}% in memory");
    }

    if (reportOnly)
    {
        Console.WriteLine(
            $"{result.Regressions.Count} regression(s) found. Report-only on this branch, so the gate does not fail.");
        return 0;
    }

    Console.Error.WriteLine(
        $"::error::Performance gate failed: {result.Regressions.Count} benchmark(s) regressed by more than {threshold:N0}%.");
    return 1;
}

static int Capture(Dictionary<string, string> options)
{
    var resultsDirectory = options.GetValueOrDefault("results", "BenchmarkDotNet.Artifacts/results");
    var outputPath = options.GetValueOrDefault("out", "benchmarks/baseline.json");

    BenchmarkRun run;
    try
    {
        run = BenchmarkResultReader.ReadRun(resultsDirectory);
    }
    catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 2;
    }

    var current = run.Measurements;

    var baseline = new Baseline
    {
        GeneratedAt = DateTimeOffset.UtcNow,
        Commit = options.GetValueOrDefault("commit"),

        // From the report, not from this process: `capture` is often run somewhere other than
        // where the benchmarks ran, and a baseline that misreports its own machine defeats
        // the cross-machine check it exists for.
        Runtime = run.Host ?? Environment.OSVersion.ToString(),
        Benchmarks = current.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
    };

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
    File.WriteAllText(
        outputPath,
        JsonSerializer.Serialize(baseline, new JsonSerializerOptions { WriteIndented = true }) + "\n");

    Console.WriteLine($"Captured {current.Count} benchmark(s) to {outputPath}");
    foreach (var (name, measurement) in baseline.Benchmarks)
    {
        Console.WriteLine(
            $"  {name,-62} {measurement.MeanNs / 1_000_000:N3} ms  {measurement.AllocatedBytes:N0} B");
    }

    return 0;
}

/// <summary>Write the markdown to the job summary and, when asked, a PR comment file.</summary>
static void Write(Dictionary<string, string> options, string markdown, bool reportOnly)
{
    var summaryPath = options.GetValueOrDefault("summary")
        ?? Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");

    if (!string.IsNullOrWhiteSpace(summaryPath))
    {
        File.AppendAllText(summaryPath, markdown + "\n");
    }

    // The workflow posts this with `gh pr comment --body-file`; writing a file rather than
    // shelling out keeps the tool free of a gh dependency and testable.
    if (options.GetValueOrDefault("comment") is { Length: > 0 } commentPath)
    {
        File.WriteAllText(commentPath, markdown);
    }

    _ = reportOnly;
}

static int Usage()
{
    Console.WriteLine(
        """
        PerfGate - compares a BenchmarkDotNet run against a recorded baseline.

          compare --results <dir> --baseline <file> [--threshold 15] [--report-only]
                  [--summary <file>] [--comment <file>]
              Exit 1 when a benchmark regressed by more than the threshold in either mean
              time or allocated memory. With --report-only it reports and exits 0.

          capture --results <dir> --out <file> [--commit <sha>]
              Record a run as the new baseline.
        """);

    return 2;
}

static Dictionary<string, string> ParseArgs(string[] args)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    for (var i = 1; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
        {
            continue;
        }

        var name = args[i][2..];
        var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
        options[name] = hasValue ? args[++i] : string.Empty;
    }

    return options;
}
