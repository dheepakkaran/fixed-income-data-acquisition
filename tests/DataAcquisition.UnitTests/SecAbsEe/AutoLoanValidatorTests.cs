using DataAcquisition.Core.Validation;
using DataAcquisition.Providers.SecAbsEe.Model;
using DataAcquisition.Providers.SecAbsEe.Validation;

namespace DataAcquisition.UnitTests.SecAbsEe;

public sealed class AutoLoanValidatorTests
{
    private static AutoLoanRecord Good(string assetNumber = "asset-1") => new()
    {
        TrustCik = "2099048",
        AssetNumber = assetNumber,
        ReportingPeriodEnd = new DateOnly(2026, 8, 31),
        ReportingPeriodBegin = new DateOnly(2026, 8, 1),
        OriginalLoanAmount = 32_914.85m,
        OriginalLoanTerm = 88,
        OriginalInterestRatePercentage = 0.0639m,
        ReportingPeriodInterestRatePercentage = 0.0639m,
        ObligorCreditScore = 867,
        BeginningBalance = 3_919.14m,
        EndingBalance = 3_527.33m,
        RemainingTermToMaturity = 10,
        CurrentDelinquencyDays = 0,
        ChargedOffPrincipalAmount = 0m,
        RecoveredAmount = 0m,
    };

    private static ValidationResult Validate(params AutoLoanRecord[] records) =>
        new AutoLoanValidator().Validate(records);

    private static long CountFor(ValidationResult result, string rule) =>
        result.Outcomes.FirstOrDefault(o => o.Rule == rule)?.Count ?? 0;

    [Fact]
    public void A_clean_batch_raises_nothing()
    {
        var result = Validate(Good("a"), Good("b"), Good("c"));

        Assert.False(result.HasErrors);
        Assert.Equal(0, result.WarningCount);
        Assert.Equal(3, result.RecordsChecked);
    }

    [Fact]
    public void Real_data_quirks_are_not_treated_as_errors()
    {
        // Each of these is a shape the SEC schema permits and real filings contain: a 0% APR
        // subvented loan, a missing delinquency status, a non-numeric credit score parsed to
        // null, and a loan that paid off and now reports a zero balance.
        var result = Validate(
            Good("zero-apr") with { OriginalInterestRatePercentage = 0m, ReportingPeriodInterestRatePercentage = 0m },
            Good("no-dq") with { CurrentDelinquencyDays = null },
            Good("no-score") with { ObligorCreditScore = null },
            Good("paid-off") with { EndingBalance = 0m, ZeroBalanceCode = 1, ZeroBalanceEffectiveDate = new DateOnly(2024, 5, 1) },
            Good("no-recovery") with { RecoveredAmount = null });

        Assert.False(result.HasErrors);
        Assert.Equal(0, result.WarningCount);
    }

    [Fact]
    public void A_negative_balance_is_an_error()
    {
        var result = Validate(Good() with { EndingBalance = -1.00m });

        Assert.True(result.HasErrors);
        Assert.Equal(1, CountFor(result, ValidationRules.ValueRange));
    }

    [Fact]
    public void An_impossible_interest_rate_is_an_error()
    {
        // Rates are fractions, so 1.5 is 150% APR.
        var result = Validate(Good() with { OriginalInterestRatePercentage = 1.5m });

        Assert.True(result.HasErrors);
    }

    [Fact]
    public void A_negative_interest_rate_is_an_error()
    {
        Assert.True(Validate(Good() with { ReportingPeriodInterestRatePercentage = -0.01m }).HasErrors);
    }

    [Fact]
    public void A_high_but_possible_rate_is_a_warning_not_an_error()
    {
        // Exeter's deep-subprime pool reaches 0.29; 0.40 is suspicious but not impossible.
        var result = Validate(Good() with { OriginalInterestRatePercentage = 0.40m });

        Assert.False(result.HasErrors);
        Assert.Equal(1, result.WarningCount);
    }

    [Fact]
    public void Deep_subprime_rates_are_accepted_without_complaint()
    {
        var result = Validate(Good() with
        {
            OriginalInterestRatePercentage = 0.2702m,
            ReportingPeriodInterestRatePercentage = 0.29m,
            ObligorCreditScore = 517,
        });

        Assert.False(result.HasErrors);
        Assert.Equal(0, result.WarningCount);
    }

    [Fact]
    public void A_missing_reporting_period_is_an_error()
    {
        var result = Validate(Good() with { ReportingPeriodEnd = default });

        Assert.True(result.HasErrors);
        Assert.Equal(1, CountFor(result, ValidationRules.DateFormat));
    }

    [Fact]
    public void A_period_that_ends_before_it_begins_is_an_error()
    {
        var result = Validate(Good() with
        {
            ReportingPeriodBegin = new DateOnly(2026, 9, 1),
            ReportingPeriodEnd = new DateOnly(2026, 8, 31),
        });

        Assert.True(result.HasErrors);
        Assert.Equal(1, CountFor(result, ValidationRules.DateFormat));
    }

    [Fact]
    public void An_empty_asset_number_is_a_required_field_error()
    {
        var result = Validate(Good() with { AssetNumber = "   " });

        Assert.True(result.HasErrors);
        Assert.Equal(1, CountFor(result, ValidationRules.RequiredField));
    }

    [Fact]
    public void A_duplicate_asset_in_the_same_period_is_an_error()
    {
        var result = Validate(Good("dup"), Good("dup"));

        Assert.True(result.HasErrors);
        Assert.Equal(1, CountFor(result, ValidationRules.DuplicateKey));
    }

    [Fact]
    public void The_same_asset_in_a_different_period_is_not_a_duplicate()
    {
        var result = Validate(
            Good("same"),
            Good("same") with
            {
                ReportingPeriodBegin = new DateOnly(2026, 7, 1),
                ReportingPeriodEnd = new DateOnly(2026, 7, 31),
            });

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void The_same_asset_in_a_different_trust_is_not_a_duplicate()
    {
        var result = Validate(Good("same"), Good("same") with { TrustCik = "2013062" });

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Duplicate_detection_spans_batches_within_a_run()
    {
        var validator = new AutoLoanValidator();

        Assert.False(validator.Validate([Good("dup")]).HasErrors);
        Assert.True(validator.Validate([Good("dup")]).HasErrors);
    }

    [Fact]
    public void Reset_clears_keys_so_a_second_run_starts_clean()
    {
        var validator = new AutoLoanValidator();
        Assert.False(validator.Validate([Good("dup")]).HasErrors);

        validator.Reset();

        Assert.False(validator.Validate([Good("dup")]).HasErrors);
    }

    [Fact]
    public void An_unenumerated_zero_balance_code_is_an_error()
    {
        // The schema enumerates 1, 2, 3, 4, 5 and 99 only.
        Assert.True(Validate(Good() with { ZeroBalanceCode = 7 }).HasErrors);
        Assert.False(Validate(Good() with { ZeroBalanceCode = 99 }).HasErrors);
    }

    [Fact]
    public void Repeated_zero_balance_codes_are_reported_as_a_warning()
    {
        var result = Validate(Good() with { ZeroBalanceCode = 1, ZeroBalanceCodeCount = 2 });

        Assert.False(result.HasErrors);
        Assert.Equal(1, result.WarningCount);
    }

    [Fact]
    public void An_out_of_band_credit_score_is_a_warning()
    {
        var result = Validate(Good() with { ObligorCreditScore = 42 });

        Assert.False(result.HasErrors);
        Assert.Equal(1, result.WarningCount);
    }

    [Fact]
    public void Several_bad_rows_are_counted_per_rule()
    {
        var result = Validate(
            Good("a") with { EndingBalance = -5m },
            Good("b") with { EndingBalance = -6m },
            Good("c") with { ReportingPeriodEnd = default },
            Good("d"));

        Assert.Equal(2, CountFor(result, ValidationRules.ValueRange));
        Assert.Equal(1, CountFor(result, ValidationRules.DateFormat));
        Assert.Equal(3, result.ErrorCount);
        Assert.Equal(4, result.RecordsChecked);
    }
}
