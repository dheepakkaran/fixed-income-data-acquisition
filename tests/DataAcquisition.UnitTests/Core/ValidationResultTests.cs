using DataAcquisition.Core.Validation;

namespace DataAcquisition.UnitTests.Core;

public sealed class ValidationResultTests
{
    [Fact]
    public void Counts_are_reported_per_rule()
    {
        var result = ValidationResult.Empty();
        result.Checked(10);
        result.Fail(ValidationRules.RequiredField, ValidationSeverity.Error, "missing assetNumber");
        result.Fail(ValidationRules.RequiredField, ValidationSeverity.Error, "missing assetNumber");
        result.Fail(ValidationRules.ValueRange, ValidationSeverity.Warning, "rate 0%");

        var outcomes = result.Outcomes.ToDictionary(o => o.Rule, StringComparer.Ordinal);

        Assert.Equal(2, outcomes[ValidationRules.RequiredField].Count);
        Assert.Equal(1, outcomes[ValidationRules.ValueRange].Count);
        Assert.Equal(2, result.ErrorCount);
        Assert.Equal(1, result.WarningCount);
        Assert.True(result.HasErrors);
        Assert.Equal(10, result.RecordsChecked);
    }

    [Fact]
    public void Merging_batches_sums_counts_and_records()
    {
        var first = ValidationResult.Empty();
        first.Checked(5);
        first.Fail(ValidationRules.DuplicateKey, ValidationSeverity.Error, "dup 1");

        var second = ValidationResult.Empty();
        second.Checked(7);
        second.Fail(ValidationRules.DuplicateKey, ValidationSeverity.Error, "dup 2");

        first.Merge(second);

        Assert.Equal(12, first.RecordsChecked);
        Assert.Equal(2, Assert.Single(first.Outcomes).Count);
    }

    [Fact]
    public void Warnings_alone_do_not_fail_a_load()
    {
        var result = ValidationResult.Empty();
        result.Checked(3);
        result.Fail(ValidationRules.ValueRange, ValidationSeverity.Warning, "0% APR, subvented promo");

        Assert.False(result.HasErrors);
        Assert.Equal(1, result.WarningCount);
    }

    [Fact]
    public void Strictest_severity_for_a_rule_wins()
    {
        var result = ValidationResult.Empty();
        result.Fail(ValidationRules.ValueRange, ValidationSeverity.Warning, "odd");
        result.Fail(ValidationRules.ValueRange, ValidationSeverity.Error, "impossible");

        Assert.True(result.HasErrors);
        Assert.Equal(ValidationSeverity.Error, Assert.Single(result.Outcomes).Severity);
    }

    [Fact]
    public void Samples_are_capped_so_a_bad_file_cannot_flood_the_log()
    {
        var result = ValidationResult.Empty();
        for (var i = 0; i < 50; i++)
        {
            result.Fail(ValidationRules.RequiredField, ValidationSeverity.Error, $"row {i}");
        }

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(50, outcome.Count);
        Assert.Equal(ValidationResult.MaxSamples, outcome.Samples.Count);
    }

    [Fact]
    public void Empty_result_describes_itself_without_breaches()
    {
        var result = ValidationResult.Empty();
        result.Checked(1_234);

        Assert.Contains("no rule breaches", result.Describe(), StringComparison.Ordinal);
        Assert.False(result.HasErrors);
    }
}
