namespace DataAcquisition.Core.Model;

/// <summary>
/// One unit of work discovered at a provider: for SEC ABS-EE a single EX-102 exhibit
/// inside one accession, for Treasury one calendar year of the par yield curve feed.
/// </summary>
/// <param name="SourceId">
/// Provider-scoped stable identity (for SEC the accession number plus exhibit file name).
/// Together with the provider name and the raw file hash this makes a run idempotent.
/// </param>
/// <param name="Uri">Absolute location the raw bytes are fetched from.</param>
/// <param name="ReportingPeriod">
/// Period the source describes. ABS-EE files are monthly and carry a period end date;
/// sources without a natural period leave this null.
/// </param>
/// <param name="SubjectId">
/// Entity the source belongs to, used to partition data (the trust CIK for ABS-EE).
/// </param>
/// <param name="Label">Human-readable name, used in logs and the report.</param>
public sealed record DataSource(
    string SourceId,
    Uri Uri,
    DateOnly? ReportingPeriod,
    string? SubjectId,
    string Label)
{
    public override string ToString() => $"{Label} [{SourceId}]";
}
