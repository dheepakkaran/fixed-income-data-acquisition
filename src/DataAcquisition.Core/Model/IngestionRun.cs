namespace DataAcquisition.Core.Model;

/// <summary>
/// Audit row for one attempt at one source. The report cites these ids so every
/// published number can be traced back to the file it came from.
/// </summary>
public sealed record IngestionRun
{
    public long Id { get; init; }
    public required string ProviderName { get; init; }
    public required string AssetClass { get; init; }
    public required string SourceId { get; init; }

    /// <summary>SHA-256 of the raw file. Null until the source has been fetched.</summary>
    public string? Sha256 { get; init; }

    public string? SubjectId { get; init; }
    public DateOnly? ReportingPeriod { get; init; }
    public IngestionRunStatus Status { get; init; } = IngestionRunStatus.Running;

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    public long RecordsParsed { get; init; }
    public long RecordsLoaded { get; init; }
    public long ValidationErrors { get; init; }
    public long ValidationWarnings { get; init; }

    public string? ErrorMessage { get; init; }

    public TimeSpan? Duration => CompletedAt is null ? null : CompletedAt - StartedAt;
}
