namespace DataAcquisition.Core.Model;

/// <summary>Per-run knobs supplied by the CLI.</summary>
public sealed record IngestionOptions
{
    /// <summary>How many of the most recent reporting periods to ingest per subject.</summary>
    public int MaxPeriods { get; init; } = 3;

    /// <summary>Re-ingest a source even when the same content hash already succeeded.</summary>
    public bool Force { get; init; }

    /// <summary>Discover and report, but neither fetch nor load.</summary>
    public bool DryRun { get; init; }

    /// <summary>Restrict the run to these subjects (trust CIKs). Empty means all configured.</summary>
    public IReadOnlyList<string> Subjects { get; init; } = [];
}
