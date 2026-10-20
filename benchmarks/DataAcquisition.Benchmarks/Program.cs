using BenchmarkDotNet.Running;
using DataAcquisition.Benchmarks;

// Suites are selected by name so CI can run the parser benchmarks without a database and
// the full set with one. `--filter *` runs everything.
//
//   dotnet run -c Release -- --filter *Parser*
//   dotnet run -c Release -- --filter *
return BenchmarkSwitcher
    .FromTypes([typeof(ParserBenchmarks), typeof(StorageBenchmarks)])
    .Run(args)
    .ToExitCode();

internal static class SummaryExtensions
{
    /// <summary>Non-zero when any benchmark failed to run, so CI notices.</summary>
    public static int ToExitCode(this IEnumerable<BenchmarkDotNet.Reports.Summary> summaries) =>
        summaries.Any(s => s.HasCriticalValidationErrors || s.Reports.Any(r => !r.Success)) ? 1 : 0;
}
