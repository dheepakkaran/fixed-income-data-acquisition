namespace DataAcquisition.UnitTests.Fakes;

/// <summary>A record type Core has never heard of.</summary>
/// <param name="Key">Natural key, used to exercise duplicate detection.</param>
/// <param name="Value">Payload checked by the value-range rule.</param>
public sealed record WidgetRecord(string Key, decimal Value);
