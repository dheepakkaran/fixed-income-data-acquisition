using BenchmarkDotNet.Attributes;
using DataAcquisition.Providers.SecAbsEe.Model;
using DataAcquisition.Providers.SecAbsEe.Parsing;

namespace DataAcquisition.Benchmarks;

/// <summary>
/// Streaming XML parser throughput over a real trimmed ABS-EE exhibit.
/// <para>
/// Both fixtures are benchmarked because the two filing agents format very differently: GM
/// Financial pretty-prints across many lines while Exeter emits one line with no whitespace.
/// A regression that only shows up on one shape would otherwise slip through.
/// </para>
/// </summary>
[Config(typeof(BenchmarkConfig))]
public class ParserBenchmarks
{
    private string _gmcarPath = string.Empty;
    private string _exeterPath = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _gmcarPath = Fixtures.Path("sec-abs-ee", "gmcar-2026-08-200.xml");
        _exeterPath = Fixtures.Path("sec-abs-ee", "eart-2026-08-200.xml");
    }

    [Benchmark(Description = "Parse 200 loans, pretty-printed exhibit")]
    public async Task<int> ParsePrettyPrinted()
    {
        var count = 0;
        await foreach (var record in AutoLoanXmlParser.ParseFileAsync(_gmcarPath, "2099048"))
        {
            count += record.AssetNumber.Length;
        }

        return count;
    }

    [Benchmark(Description = "Parse 200 loans, single-line exhibit")]
    public async Task<int> ParseSingleLine()
    {
        var count = 0;
        await foreach (var record in AutoLoanXmlParser.ParseFileAsync(_exeterPath, "2013062"))
        {
            count += record.AssetNumber.Length;
        }

        return count;
    }

    [Benchmark(Description = "Parse a year of the Treasury curve")]
    public async Task<int> ParseTreasuryCurve()
    {
        var count = 0;
        await foreach (var point in DataAcquisition.Providers.TreasuryYields.Parsing.TreasuryYieldXmlParser.ParseFileAsync(
            Fixtures.Path("treasury", "par-yields-2026-sample.xml")))
        {
            count += point.TenorLabel.Length;
        }

        return count;
    }

    /// <summary>Keeps the compiler from eliding the parse when a result is unused.</summary>
    [Benchmark(Description = "Parse and total balances, pretty-printed exhibit")]
    public async Task<decimal> ParseAndAggregate()
    {
        var total = 0m;
        await foreach (var record in AutoLoanXmlParser.ParseFileAsync(_gmcarPath, "2099048"))
        {
            total += record.EndingBalance ?? 0m;
        }

        return total;
    }
}
