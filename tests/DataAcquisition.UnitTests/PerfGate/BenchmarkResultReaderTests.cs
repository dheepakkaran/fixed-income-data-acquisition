using PerfGate;

namespace DataAcquisition.UnitTests.PerfGate;

public sealed class BenchmarkResultReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "perfgate-tests", Guid.NewGuid().ToString("N"));

    public BenchmarkResultReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void WriteReport(string name, string json) =>
        File.WriteAllText(Path.Combine(_directory, $"{name}-report-full.json"), json);

    [Fact]
    public void Reads_the_mean_and_allocation_of_each_benchmark()
    {
        WriteReport("Suite", """
            {
              "Benchmarks": [
                {
                  "Type": "ParserBenchmarks", "Method": "ParsePrettyPrinted", "Parameters": "",
                  "Statistics": { "Mean": 1360000.0 },
                  "Memory": { "BytesAllocatedPerOperation": 2024232 }
                }
              ]
            }
            """);

        var results = BenchmarkResultReader.Read(_directory);

        var measurement = Assert.Single(results);
        Assert.Equal("ParserBenchmarks.ParsePrettyPrinted", measurement.Key);
        Assert.Equal(1_360_000.0, measurement.Value.MeanNs);
        Assert.Equal(2_024_232, measurement.Value.AllocatedBytes);
    }

    [Fact]
    public void Parameterised_benchmarks_get_distinct_keys()
    {
        // Records=10000 and a future Records=50000 must not collide into one baseline entry.
        WriteReport("Suite", """
            {
              "Benchmarks": [
                { "Type": "StorageBenchmarks", "Method": "CopyLoad", "Parameters": "Records=10000",
                  "Statistics": { "Mean": 84786000.0 }, "Memory": { "BytesAllocatedPerOperation": 3392560 } },
                { "Type": "StorageBenchmarks", "Method": "CopyLoad", "Parameters": "Records=50000",
                  "Statistics": { "Mean": 400000000.0 }, "Memory": { "BytesAllocatedPerOperation": 9000000 } }
              ]
            }
            """);

        var results = BenchmarkResultReader.Read(_directory);

        Assert.Equal(2, results.Count);
        Assert.Contains("StorageBenchmarks.CopyLoad[Records=10000]", results.Keys);
        Assert.Contains("StorageBenchmarks.CopyLoad[Records=50000]", results.Keys);
    }

    [Fact]
    public void Several_suites_are_merged()
    {
        WriteReport("A", """{"Benchmarks":[{"Type":"X","Method":"m1","Statistics":{"Mean":1}}]}""");
        WriteReport("B", """{"Benchmarks":[{"Type":"Y","Method":"m2","Statistics":{"Mean":2}}]}""");

        var results = BenchmarkResultReader.Read(_directory);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void A_benchmark_without_memory_diagnostics_still_reads()
    {
        WriteReport("Suite", """{"Benchmarks":[{"Type":"X","Method":"m","Statistics":{"Mean":5}}]}""");

        Assert.Null(Assert.Single(BenchmarkResultReader.Read(_directory)).Value.AllocatedBytes);
    }

    [Fact]
    public void A_missing_directory_is_an_error_rather_than_an_empty_pass()
    {
        // Silently reporting zero benchmarks would let a broken benchmark step pass the gate.
        Assert.Throws<DirectoryNotFoundException>(() =>
            BenchmarkResultReader.Read(Path.Combine(_directory, "nope")));
    }

    [Fact]
    public void A_directory_with_no_reports_is_an_error()
    {
        Assert.Throws<FileNotFoundException>(() => BenchmarkResultReader.Read(_directory));
    }
}
