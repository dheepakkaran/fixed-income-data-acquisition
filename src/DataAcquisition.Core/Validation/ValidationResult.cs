using System.Collections.Concurrent;

namespace DataAcquisition.Core.Validation;

/// <summary>
/// Mergeable tally of rule breaches. The pipeline validates in batches so memory stays
/// flat on a large file, then merges each batch's result into one per-source total.
/// </summary>
public sealed class ValidationResult
{
    /// <summary>Example messages retained per rule.</summary>
    public const int MaxSamples = 5;

    private readonly ConcurrentDictionary<string, Tally> _tallies = new(StringComparer.Ordinal);

    private sealed class Tally
    {
        public ValidationSeverity Severity;
        public long Count;
        public readonly List<string> Samples = [];
    }

    /// <summary>Records seen, whether or not they broke a rule.</summary>
    public long RecordsChecked { get; private set; }

    public static ValidationResult Empty() => new();

    /// <summary>Note that a record passed through, without flagging anything.</summary>
    public void Checked(long count = 1) => RecordsChecked += count;

    /// <summary>Record one breach of <paramref name="rule"/>.</summary>
    public void Fail(string rule, ValidationSeverity severity, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);

        var tally = _tallies.GetOrAdd(rule, _ => new Tally { Severity = severity });
        lock (tally)
        {
            // A rule reported at mixed severities takes the strictest seen, so one genuinely
            // bad record cannot be masked by warnings for the same rule.
            if (severity > tally.Severity)
            {
                tally.Severity = severity;
            }

            tally.Count++;
            if (tally.Samples.Count < MaxSamples)
            {
                tally.Samples.Add(message);
            }
        }
    }

    /// <summary>Fold <paramref name="other"/> into this result.</summary>
    public void Merge(ValidationResult other)
    {
        ArgumentNullException.ThrowIfNull(other);

        RecordsChecked += other.RecordsChecked;
        foreach (var (rule, incoming) in other._tallies)
        {
            var tally = _tallies.GetOrAdd(rule, _ => new Tally { Severity = incoming.Severity });
            lock (tally)
            {
                if (incoming.Severity > tally.Severity)
                {
                    tally.Severity = incoming.Severity;
                }

                tally.Count += incoming.Count;
                foreach (var sample in incoming.Samples)
                {
                    if (tally.Samples.Count >= MaxSamples)
                    {
                        break;
                    }

                    tally.Samples.Add(sample);
                }
            }
        }
    }

    /// <summary>Per-rule counts, worst severity first then most frequent.</summary>
    public IReadOnlyList<RuleOutcome> Outcomes =>
        [.. _tallies
            .Select(kv => new RuleOutcome(kv.Key, kv.Value.Severity, kv.Value.Count, kv.Value.Samples.ToArray()))
            .OrderByDescending(o => o.Severity)
            .ThenByDescending(o => o.Count)
            .ThenBy(o => o.Rule, StringComparer.Ordinal)];

    public long ErrorCount => _tallies.Values
        .Where(t => t.Severity == ValidationSeverity.Error)
        .Sum(t => t.Count);

    public long WarningCount => _tallies.Values
        .Where(t => t.Severity == ValidationSeverity.Warning)
        .Sum(t => t.Count);

    /// <summary>True when the load must be abandoned.</summary>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>One-line summary for the run log.</summary>
    public string Describe() => Outcomes.Count == 0
        ? $"{RecordsChecked:N0} records, no rule breaches"
        : $"{RecordsChecked:N0} records, " +
          string.Join(", ", Outcomes.Select(o => $"{o.Rule}={o.Count:N0}({o.Severity.ToString()[..1]})"));
}
