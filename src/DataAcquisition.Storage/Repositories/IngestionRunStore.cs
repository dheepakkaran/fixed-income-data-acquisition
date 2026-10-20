using Dapper;
using DataAcquisition.Core.Abstractions;
using DataAcquisition.Core.Model;
using Npgsql;

namespace DataAcquisition.Storage.Repositories;

/// <summary>
/// Dapper-backed run tracking. SQL is written out in full rather than generated, so the
/// queries behind the audit trail stay reviewable.
/// </summary>
public sealed class IngestionRunStore(NpgsqlDataSource dataSource) : IIngestionRunStore
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private const string SelectColumns = """
        id                  as Id,
        provider_name       as ProviderName,
        asset_class         as AssetClass,
        source_id           as SourceId,
        sha256              as Sha256,
        subject_id          as SubjectId,
        reporting_period    as ReportingPeriod,
        status              as Status,
        started_at          as StartedAt,
        completed_at        as CompletedAt,
        records_parsed      as RecordsParsed,
        records_loaded      as RecordsLoaded,
        validation_errors   as ValidationErrors,
        validation_warnings as ValidationWarnings,
        error_message       as ErrorMessage
        """;

    public async Task<IngestionRun> StartAsync(IngestionRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        const string sql = """
            insert into ingestion_runs
                (provider_name, asset_class, source_id, sha256, subject_id,
                 reporting_period, status, started_at)
            values
                (@ProviderName, @AssetClass, @SourceId, @Sha256, @SubjectId,
                 @ReportingPeriod, @Status, @StartedAt)
            returning id
            """;

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            sql,
            new
            {
                run.ProviderName,
                run.AssetClass,
                run.SourceId,
                run.Sha256,
                run.SubjectId,
                run.ReportingPeriod,
                Status = (short)run.Status,
                run.StartedAt,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return run with { Id = id };
    }

    public async Task CompleteAsync(IngestionRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        const string sql = """
            update ingestion_runs
            set status              = @Status,
                completed_at        = @CompletedAt,
                records_parsed      = @RecordsParsed,
                records_loaded      = @RecordsLoaded,
                validation_errors   = @ValidationErrors,
                validation_warnings = @ValidationWarnings,
                error_message       = @ErrorMessage,
                sha256              = coalesce(@Sha256, sha256)
            where id = @Id
            """;

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                run.Id,
                Status = (short)run.Status,
                run.CompletedAt,
                run.RecordsParsed,
                run.RecordsLoaded,
                run.ValidationErrors,
                run.ValidationWarnings,
                run.ErrorMessage,
                run.Sha256,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> IsAlreadyIngestedAsync(
        string providerName,
        string sourceId,
        string sha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);

        // Matches ix_ingestion_runs_identity, which is partial on status = 1 (Succeeded).
        const string sql = """
            select exists (
                select 1 from ingestion_runs
                where provider_name = @providerName
                  and source_id     = @sourceId
                  and sha256        = @sha256
                  and status        = 1
            )
            """;

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            sql,
            new { providerName, sourceId, sha256 },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IngestionRun>> GetRunsAsync(
        string? providerName,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var sql = $"""
            select {SelectColumns}
            from ingestion_runs
            where (@providerName is null or provider_name = @providerName)
            order by id desc
            limit @limit
            """;

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = await connection.QueryAsync<IngestionRun>(new CommandDefinition(
            sql,
            new { providerName, limit },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows];
    }

    /// <summary>Record that a raw file was seen upstream. Idempotent on content hash.</summary>
    public async Task RecordSourceFileAsync(
        string providerName,
        DataSource source,
        string sha256,
        long sizeBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        const string sql = """
            insert into source_files
                (provider_name, source_id, sha256, uri, size_bytes, subject_id, reporting_period)
            values
                (@providerName, @sourceId, @sha256, @uri, @sizeBytes, @subjectId, @reportingPeriod)
            on conflict (provider_name, source_id, sha256)
            do update set last_fetched_at = now()
            """;

        await using var connection = await _dataSource
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                providerName,
                sourceId = source.SourceId,
                sha256,
                uri = source.Uri.ToString(),
                sizeBytes,
                subjectId = source.SubjectId,
                reportingPeriod = source.ReportingPeriod,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
