using DataAcquisition.Core.Validation;
using DataAcquisition.Providers.TreasuryYields.Model;
using DataAcquisition.Providers.TreasuryYields.Validation;

namespace DataAcquisition.UnitTests.TreasuryYields;

public sealed class TreasuryYieldValidatorTests
{
    private static TreasuryParYield Good(decimal tenor = 24m, decimal rate = 4.14m) =>
        new(new DateOnly(2026, 6, 30), tenor, "2Y", rate);

    private static ValidationResult Validate(params TreasuryParYield[] points) =>
        new TreasuryYieldValidator().Validate(points);

    [Fact]
    public void A_clean_curve_raises_nothing()
    {
        var result = Validate(Good(24m), Good(36m, 4.15m), Good(60m, 4.19m));

        Assert.False(result.HasErrors);
        Assert.Equal(0, result.WarningCount);
    }

    [Fact]
    public void An_implausible_rate_is_an_error()
    {
        Assert.True(Validate(Good(rate: 99m)).HasErrors);
        Assert.True(Validate(Good(rate: -10m)).HasErrors);
    }

    [Fact]
    public void A_rate_that_looks_like_a_fraction_is_warned_about()
    {
        // 0.0414 would be 4.14% expressed the ABS-EE way. Treasury publishes percentages, so
        // a value that small means the two scales have been mixed up somewhere.
        var result = Validate(Good(rate: 0.000414m));

        Assert.False(result.HasErrors);
        Assert.Equal(1, result.WarningCount);
    }

    [Fact]
    public void A_missing_quote_date_is_an_error()
    {
        Assert.True(Validate(new TreasuryParYield(default, 24m, "2Y", 4.14m)).HasErrors);
    }

    [Fact]
    public void A_non_positive_tenor_is_an_error()
    {
        Assert.True(Validate(Good(tenor: 0m)).HasErrors);
    }

    [Fact]
    public void A_repeated_date_and_tenor_is_a_duplicate()
    {
        var result = Validate(Good(24m), Good(24m));

        Assert.True(result.HasErrors);
        Assert.Equal(1, result.Outcomes.First(o => o.Rule == ValidationRules.DuplicateKey).Count);
    }

    [Fact]
    public void The_same_tenor_on_another_day_is_not_a_duplicate()
    {
        var result = Validate(
            Good(24m),
            new TreasuryParYield(new DateOnly(2026, 7, 31), 24m, "2Y", 4.28m));

        Assert.False(result.HasErrors);
    }

    [Fact]
    public void Reset_clears_keys_between_runs()
    {
        var validator = new TreasuryYieldValidator();
        Assert.False(validator.Validate([Good()]).HasErrors);
        validator.Reset();
        Assert.False(validator.Validate([Good()]).HasErrors);
    }
}
