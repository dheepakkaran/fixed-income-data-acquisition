namespace DataAcquisition.Core.Model;

/// <summary>Terminal and in-flight states of one source's ingestion.</summary>
public enum IngestionRunStatus
{
    /// <summary>Row written, work not finished. A run left here means the process died mid-flight.</summary>
    Running = 0,

    /// <summary>Parsed, validated and loaded.</summary>
    Succeeded = 1,

    /// <summary>Validation or load failed. Nothing was committed to the target tables.</summary>
    Failed = 2,

    /// <summary>This exact provider + source + content hash was already ingested successfully.</summary>
    Skipped = 3,
}
