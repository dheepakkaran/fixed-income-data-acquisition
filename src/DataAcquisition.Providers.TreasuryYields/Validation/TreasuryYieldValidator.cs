using System.Globalization;
using System.Runtime.CompilerServices;
using DataAcquisition.Core.Validation;
using DataAcquisition.Providers.TreasuryYields.Model;

namespace DataAcquisition.Providers.TreasuryYields.Validation;

/// <summary>Rules for par yield points.</summary>
public sealed class TreasuryYieldValidator
{
    /// <summary>Below this a published par yield is implausible rather than merely unusual.</summary>
    private const decimal MinRate = -5m;

    /// <summary>Above this likewise. The 1981 peak was about 15%.</summary>
    private const decimal MaxRate = 25m;

    private readonly HashSet<(DateOnly Date, decimal Tenor)> _seen = [];

    /// <summary>Forget previously seen keys at the start of a run.</summary>
    public void Reset() => _seen.Clear();

    public ValidationResult Validate(IReadOnlyList<TreasuryParYield> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var result = ValidationResult.Empty();
        result.Checked(batch.Count);

        foreach (var point in batch)
        {
            if (point.QuoteDate == default)
            {
                result.Fail(
                    ValidationRules.DateFormat,
                    ValidationSeverity.Error,
                    "quote date missing or unparseable");
                continue;
            }

            if (string.IsNullOrWhiteSpace(point.TenorLabel))
            {
                result.Fail(ValidationRules.RequiredField, ValidationSeverity.Error, "tenor label is empty");
            }

            if (point.TenorMonths <= 0)
            {
                result.Fail(
                    ValidationRules.ValueRange,
                    ValidationSeverity.Error,
                    Format($"{point.QuoteDate} {point.TenorLabel}: tenor {point.TenorMonths} months is not positive"));
            }

            if (point.RatePercent < MinRate || point.RatePercent > MaxRate)
            {
                result.Fail(
                    ValidationRules.ValueRange,
                    ValidationSeverity.Error,
                    Format($"{point.QuoteDate} {point.TenorLabel}: rate {point.RatePercent}% is outside {MinRate}-{MaxRate}"));
            }

            // A par yield is a percentage. A value below 0.001 would mean a curve quoted as
            // a fraction, which is the scale confusion this whole project has to avoid.
            if (point.RatePercent is > 0m and < 0.001m)
            {
                result.Fail(
                    ValidationRules.ValueRange,
                    ValidationSeverity.Warning,
                    Format($"{point.QuoteDate} {point.TenorLabel}: rate {point.RatePercent} looks like a fraction, not a percentage"));
            }

            if (!_seen.Add((point.QuoteDate, point.TenorMonths)))
            {
                result.Fail(
                    ValidationRules.DuplicateKey,
                    ValidationSeverity.Error,
                    Format($"{point.QuoteDate} {point.TenorLabel}: already seen"));
            }
        }

        return result;
    }

    private static string Format(ref DefaultInterpolatedStringHandler handler) =>
        string.Create(CultureInfo.InvariantCulture, ref handler);
}
