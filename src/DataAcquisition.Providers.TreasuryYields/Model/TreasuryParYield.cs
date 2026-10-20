namespace DataAcquisition.Providers.TreasuryYields.Model;

/// <summary>
/// One point on the U.S. Treasury daily par yield curve.
/// </summary>
/// <param name="QuoteDate">Business day the curve was published for (<c>d:NEW_DATE</c>).</param>
/// <param name="TenorMonths">
/// Tenor in months. Stored as the sort key because the feed's field names
/// (<c>BC_1_5MONTH</c>, <c>BC_30YEAR</c>) do not sort into curve order as text.
/// </param>
/// <param name="TenorLabel">Human-readable tenor for the report, such as <c>2Y</c> or <c>1.5M</c>.</param>
/// <param name="RatePercent">
/// Par yield as a PERCENTAGE, which is how Treasury publishes it: 4.19 means 4.19%.
/// Note that ABS-EE loan rates are decimal fractions, so the spread metric converts.
/// </param>
public sealed record TreasuryParYield(
    DateOnly QuoteDate,
    decimal TenorMonths,
    string TenorLabel,
    decimal RatePercent);
