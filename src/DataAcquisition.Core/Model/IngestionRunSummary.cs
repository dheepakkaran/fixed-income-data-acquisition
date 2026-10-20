namespace DataAcquisition.Core.Model;

/// <summary>Outcome of one provider's full pass over all its discovered sources.</summary>
/// <param name="ProviderName">Provider that produced this summary.</param>
/// <param name="Runs">One entry per source considered, including skipped ones.</param>
public sealed record IngestionRunSummary(string ProviderName, IReadOnlyList<IngestionRun> Runs)
{
    public int Succeeded => Runs.Count(r => r.Status == IngestionRunStatus.Succeeded);
    public int Failed => Runs.Count(r => r.Status == IngestionRunStatus.Failed);
    public int Skipped => Runs.Count(r => r.Status == IngestionRunStatus.Skipped);
    public long RecordsLoaded => Runs.Sum(r => r.RecordsLoaded);

    /// <summary>True when no source failed. The CLI turns this into its exit code.</summary>
    public bool IsSuccess => Failed == 0;
}
