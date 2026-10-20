namespace DataAcquisition.IntegrationTests.Fakes;

/// <summary>
/// Stand-in record for the loader tests. Shaped like a real loan-level row: a three-part
/// natural key, a high-precision decimal and a nullable integer.
/// </summary>
public sealed record WidgetRow(
    string Trust,
    DateOnly Period,
    string AssetId,
    decimal? Balance,
    int? Score);
