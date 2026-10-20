using System.Text.Json.Serialization;

namespace PerfGate.Model;

/// <summary>One benchmark's recorded performance.</summary>
/// <param name="MeanNs">Mean time per operation, nanoseconds.</param>
/// <param name="AllocatedBytes">Bytes allocated per operation, or null when not measured.</param>
public sealed record BenchmarkMeasurement(
    [property: JsonPropertyName("meanNs")] double MeanNs,
    [property: JsonPropertyName("allocatedBytes")] long? AllocatedBytes);

/// <summary>
/// The performance a change is measured against. Refreshed from <c>main</c> after a release
/// so the next comparison starts from what production actually ships.
/// </summary>
public sealed record Baseline
{
    [JsonPropertyName("generatedAt")]
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Commit the baseline was captured at, so a stale baseline is identifiable.</summary>
    [JsonPropertyName("commit")]
    public string? Commit { get; init; }

    /// <summary>Machine the baseline was captured on. Comparisons across machines are not meaningful.</summary>
    [JsonPropertyName("runtime")]
    public string? Runtime { get; init; }

    [JsonPropertyName("benchmarks")]
    public Dictionary<string, BenchmarkMeasurement> Benchmarks { get; init; } = [];
}
