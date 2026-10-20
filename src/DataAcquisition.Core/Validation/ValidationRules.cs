namespace DataAcquisition.Core.Validation;

/// <summary>
/// Rule categories every provider reports against, so per-rule counts are comparable
/// across providers and asset classes.
/// </summary>
public static class ValidationRules
{
    public const string RequiredField = "required-field";
    public const string ValueRange = "value-range";
    public const string DuplicateKey = "duplicate-key";
    public const string DateFormat = "date-format";
}
