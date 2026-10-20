using DataAcquisition.Core.Model;

namespace DataAcquisition.Core.Abstractions;

/// <summary>Persistence for the ingestion audit trail.</summary>
public interface IIngestionRunStore
{
    /// <summary>Write a <see cref="IngestionRunStatus.Running"/> row and return it with its id.</summary>
    Task<IngestionRun> StartAsync(IngestionRun run, CancellationToken cancellationToken);

    /// <summary>Write the terminal state of a run.</summary>
    Task CompleteAsync(IngestionRun run, CancellationToken cancellationToken);

    /// <summary>
    /// True when this provider + source + content hash already completed successfully.
    /// The hash is what makes the check content-based rather than name-based: a trust that
    /// refiles a corrected exhibit under the same accession number is treated as new work.
    /// </summary>
    Task<bool> IsAlreadyIngestedAsync(
        string providerName,
        string sourceId,
        string sha256,
        CancellationToken cancellationToken);

    /// <summary>Runs behind a reporting period, newest first, for report provenance.</summary>
    Task<IReadOnlyList<IngestionRun>> GetRunsAsync(
        string? providerName,
        int limit,
        CancellationToken cancellationToken);
}
