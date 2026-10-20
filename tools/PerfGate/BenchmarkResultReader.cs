using System.Globalization;
using System.Text.Json;
using PerfGate.Model;

namespace PerfGate;

/// <summary>Reads BenchmarkDotNet's full JSON export.</summary>
public static class BenchmarkResultReader
{
    /// <summary>
    /// Load every <c>*-report-full.json</c> under <paramref name="directory"/> and flatten
    /// them into one measurement per benchmark, keyed by
    /// <c>Type.Method</c> plus any parameters.
    /// </summary>
    public static Dictionary<string, BenchmarkMeasurement> Read(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"No benchmark results at '{directory}'. Run the benchmarks before the gate.");
        }

        var files = Directory
            .EnumerateFiles(directory, "*-report-full.json", SearchOption.AllDirectories)
            .ToArray();

        if (files.Length == 0)
        {
            throw new FileNotFoundException(
                $"No '*-report-full.json' under '{directory}'. The JsonExporter.Full exporter must be enabled.");
        }

        var measurements = new Dictionary<string, BenchmarkMeasurement>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));

            if (!document.RootElement.TryGetProperty("Benchmarks", out var benchmarks))
            {
                continue;
            }

            foreach (var benchmark in benchmarks.EnumerateArray())
            {
                var key = KeyFor(benchmark);
                if (key is null)
                {
                    continue;
                }

                if (!benchmark.TryGetProperty("Statistics", out var statistics)
                    || statistics.ValueKind != JsonValueKind.Object
                    || !statistics.TryGetProperty("Mean", out var mean))
                {
                    continue;
                }

                long? allocated = benchmark.TryGetProperty("Memory", out var memory)
                    && memory.ValueKind == JsonValueKind.Object
                    && memory.TryGetProperty("BytesAllocatedPerOperation", out var bytes)
                    && bytes.ValueKind == JsonValueKind.Number
                        ? bytes.GetInt64()
                        : null;

                measurements[key] = new BenchmarkMeasurement(mean.GetDouble(), allocated);
            }
        }

        return measurements;
    }

    /// <summary>
    /// A stable key: the type and method, plus parameters when the benchmark is
    /// parameterised, so <c>Records=10000</c> and a future <c>Records=50000</c> do not
    /// collide.
    /// </summary>
    private static string? KeyFor(JsonElement benchmark)
    {
        var type = benchmark.TryGetProperty("Type", out var t) ? t.GetString() : null;
        var method = benchmark.TryGetProperty("Method", out var m) ? m.GetString() : null;

        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(method))
        {
            return null;
        }

        var parameters = benchmark.TryGetProperty("Parameters", out var p) ? p.GetString() : null;

        return string.IsNullOrWhiteSpace(parameters)
            ? string.Create(CultureInfo.InvariantCulture, $"{type}.{method}")
            : string.Create(CultureInfo.InvariantCulture, $"{type}.{method}[{parameters}]");
    }
}
