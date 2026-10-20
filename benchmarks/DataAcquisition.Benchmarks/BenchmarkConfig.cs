using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;

namespace DataAcquisition.Benchmarks;

/// <summary>
/// Benchmark configuration shared by every suite.
/// <para>
/// A short job rather than BenchmarkDotNet's defaults: these run on every release PR on a
/// shared GitHub runner, and a full run would take tens of minutes for precision the gate's
/// 15% threshold does not use. The trade-off is wider variance, which is exactly why the
/// threshold is 15% and not 2%.
/// </para>
/// </summary>
public sealed class BenchmarkConfig : ManualConfig
{
    public BenchmarkConfig()
    {
        AddJob(Job.ShortRun
            .WithWarmupCount(2)
            .WithIterationCount(5)
            .WithLaunchCount(1));

        // MemoryDiagnoser supplies the allocated-bytes column the gate compares.
        AddDiagnoser(MemoryDiagnoser.Default);

        AddColumnProvider(DefaultColumnProviders.Instance);
        AddLogger(ConsoleLogger.Default);

        // The gate reads the full JSON export.
        AddExporter(JsonExporter.Full);

        WithOptions(ConfigOptions.DisableOptimizationsValidator);
    }
}
