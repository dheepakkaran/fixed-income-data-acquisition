namespace DataAcquisition.Core.Validation;

/// <summary>Per-rule tally across one source.</summary>
/// <param name="Rule">Rule category, normally one of <see cref="ValidationRules"/>.</param>
/// <param name="Severity">Whether breaches abort the load.</param>
/// <param name="Count">How many records breached the rule.</param>
/// <param name="Samples">
/// Up to <see cref="ValidationResult.MaxSamples"/> example messages, kept so a failure is
/// diagnosable from the run log without re-reading a 140 MB file.
/// </param>
public sealed record RuleOutcome(
    string Rule,
    ValidationSeverity Severity,
    long Count,
    IReadOnlyList<string> Samples);
