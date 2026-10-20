namespace DataAcquisition.Core.Validation;

/// <summary>
/// How a broken rule is treated. Real filings are untidy in ways the SEC schema permits
/// (ABS-EE marks nearly every element minOccurs="0"), so a provider marks genuinely
/// unusable data as <see cref="Error"/> and merely notable data as <see cref="Warning"/>.
/// </summary>
public enum ValidationSeverity
{
    /// <summary>Counted and reported; the load proceeds.</summary>
    Warning = 0,

    /// <summary>Aborts the load for this source and marks the run Failed.</summary>
    Error = 1,
}
