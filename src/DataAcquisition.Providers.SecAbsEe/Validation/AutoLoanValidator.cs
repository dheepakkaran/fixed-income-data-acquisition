using System.Globalization;
using System.Runtime.CompilerServices;
using DataAcquisition.Core.Validation;
using DataAcquisition.Providers.SecAbsEe.Model;

namespace DataAcquisition.Providers.SecAbsEe.Validation;

/// <summary>
/// Rules for loan-level auto loan records.
/// <para>
/// Thresholds come from the real filings rather than intuition. Rates are decimal fractions
/// (0.0639 = 6.39% APR), so the sane band is expressed in those units; 0% is legitimate and
/// not flagged at all, because subvented promotional financing really is 0% and eight of the
/// first two hundred GM Financial loans are.
/// </para>
/// </summary>
public sealed class AutoLoanValidator
{
    /// <summary>Above this the rate is impossible rather than merely unusual.</summary>
    private const decimal MaxPlausibleRate = 1.0m;

    /// <summary>
    /// Above this a rate is worth reporting but not refusing. Exeter's deep-subprime pool
    /// reaches 0.29, so the warning line sits above that.
    /// </summary>
    private const decimal HighRate = 0.35m;

    private const int MinCreditScore = 250;
    private const int MaxCreditScore = 900;

    /// <summary>
    /// Keys seen in this run, so a duplicate is caught before the database rejects it.
    /// Keyed by trust, period and asset number, which also catches the same loan arriving
    /// twice from two files.
    /// </summary>
    private readonly HashSet<(string Trust, DateOnly Period, string Asset)> _seen = [];

    /// <summary>
    /// Forget previously seen keys. The provider calls this when a run begins, since the
    /// provider is a singleton and a second run must not inherit the first run's keys.
    /// </summary>
    public void Reset() => _seen.Clear();

    public ValidationResult Validate(IReadOnlyList<AutoLoanRecord> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var result = ValidationResult.Empty();
        result.Checked(batch.Count);

        foreach (var record in batch)
        {
            ValidateOne(record, result);
        }

        return result;
    }

    private void ValidateOne(AutoLoanRecord record, ValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(record.AssetNumber))
        {
            result.Fail(ValidationRules.RequiredField, ValidationSeverity.Error, "assetNumber is empty");
            return;
        }

        // reportingPeriodEndingDate is optional in the schema but the natural key needs it,
        // and a row without one cannot be attributed to a period in the report.
        if (record.ReportingPeriodEnd == default)
        {
            result.Fail(
                ValidationRules.DateFormat,
                ValidationSeverity.Error,
                $"{record.AssetNumber}: reportingPeriodEndingDate missing or unparseable");
        }

        if (record.ReportingPeriodBegin is { } begin
            && record.ReportingPeriodEnd != default
            && begin > record.ReportingPeriodEnd)
        {
            result.Fail(
                ValidationRules.DateFormat,
                ValidationSeverity.Error,
                $"{record.AssetNumber}: reporting period begins {begin} after it ends {record.ReportingPeriodEnd}");
        }

        if (record.EndingBalance is { } ending && ending < 0)
        {
            result.Fail(
                ValidationRules.ValueRange,
                ValidationSeverity.Error,
                Format($"{record.AssetNumber}: ending balance {ending} is negative"));
        }

        if (record.BeginningBalance is { } beginning && beginning < 0)
        {
            result.Fail(
                ValidationRules.ValueRange,
                ValidationSeverity.Error,
                Format($"{record.AssetNumber}: beginning balance {beginning} is negative"));
        }

        CheckRate(record, record.OriginalInterestRatePercentage, "original", result);
        CheckRate(record, record.ReportingPeriodInterestRatePercentage, "reporting period", result);

        if (record.OriginalLoanTerm is { } term && term is < 0 or > 240)
        {
            result.Fail(
                ValidationRules.ValueRange,
                ValidationSeverity.Warning,
                Format($"{record.AssetNumber}: original loan term {term} months is outside 0-240"));
        }

        // A null score is normal and not flagged: the schema types the element as a string
        // and filers write non-numeric values into it.
        if (record.ObligorCreditScore is { } score && score is < MinCreditScore or > MaxCreditScore)
        {
            result.Fail(
                ValidationRules.ValueRange,
                ValidationSeverity.Warning,
                Format($"{record.AssetNumber}: credit score {score} is outside {MinCreditScore}-{MaxCreditScore}"));
        }

        if (record.CurrentDelinquencyDays is { } days && days < 0)
        {
            result.Fail(
                ValidationRules.ValueRange,
                ValidationSeverity.Error,
                Format($"{record.AssetNumber}: delinquency {days} days is negative"));
        }

        // The schema allows zeroBalanceCode to repeat; the parser keeps the first. Report it
        // rather than discard the extras silently.
        if (record.ZeroBalanceCodeCount > 1)
        {
            result.Fail(
                ValidationRules.ValueRange,
                ValidationSeverity.Warning,
                Format($"{record.AssetNumber}: {record.ZeroBalanceCodeCount} zeroBalanceCode elements, first kept"));
        }

        if (record.ZeroBalanceCode is { } code && code is not (1 or 2 or 3 or 4 or 5 or 99))
        {
            result.Fail(
                ValidationRules.ValueRange,
                ValidationSeverity.Error,
                Format($"{record.AssetNumber}: zeroBalanceCode {code} is not an enumerated value"));
        }

        if (record.ReportingPeriodEnd != default
            && !_seen.Add((record.TrustCik, record.ReportingPeriodEnd, record.AssetNumber)))
        {
            result.Fail(
                ValidationRules.DuplicateKey,
                ValidationSeverity.Error,
                $"{record.AssetNumber}: already seen for {record.TrustCik} period {record.ReportingPeriodEnd}");
        }
    }

    private static void CheckRate(
        AutoLoanRecord record,
        decimal? rate,
        string label,
        ValidationResult result)
    {
        if (rate is not { } value)
        {
            return;
        }

        if (value < 0 || value > MaxPlausibleRate)
        {
            result.Fail(
                ValidationRules.ValueRange,
                ValidationSeverity.Error,
                Format($"{record.AssetNumber}: {label} rate {value} is outside 0-{MaxPlausibleRate}"));
        }
        else if (value > HighRate)
        {
            result.Fail(
                ValidationRules.ValueRange,
                ValidationSeverity.Warning,
                Format($"{record.AssetNumber}: {label} rate {value} is unusually high"));
        }
    }

    private static string Format(ref DefaultInterpolatedStringHandler handler) =>
        string.Create(CultureInfo.InvariantCulture, ref handler);
}
