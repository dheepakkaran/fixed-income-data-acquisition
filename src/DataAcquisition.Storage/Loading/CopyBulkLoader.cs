using System.Globalization;
using DataAcquisition.Core.Abstractions;
using DataAcquisition.Core.Model;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DataAcquisition.Storage.Loading;

/// <summary>
/// Loads records with Npgsql binary COPY into a temporary staging table, then merges the
/// staging table into the target with a single
/// <c>INSERT ... ON CONFLICT DO UPDATE</c>, all inside one transaction.
/// <para>
/// COPY is used because loan-level files carry tens of thousands of rows per trust per
/// month and row-by-row INSERT is too slow; staging plus one upsert is used because it
/// makes the whole source atomic and re-runnable, so a re-run updates rows in place
/// instead of duplicating them.
/// </para>
/// </summary>
/// <typeparam name="TRecord">Record type being written.</typeparam>
public sealed class CopyBulkLoader<TRecord>(
    NpgsqlDataSource dataSource,
    IPostgresTableMap<TRecord> map,
    ILogger<CopyBulkLoader<TRecord>> logger) : IBulkLoader<TRecord>
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private readonly IPostgresTableMap<TRecord> _map = map ?? throw new ArgumentNullException(nameof(map));

    private readonly ILogger<CopyBulkLoader<TRecord>> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public string Strategy => "binary-copy";

    public async Task<IRecordSinkSession<TRecord>> BeginAsync(
        DataSource source,
        CancellationToken cancellationToken)
    {
        var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            var session = new Session(connection, transaction, _map, _logger);
            await session.CreateStagingTableAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Session(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IPostgresTableMap<TRecord> map,
        ILogger logger) : IRecordSinkSession<TRecord>
    {
        private readonly string _staging = $"staging_{map.TargetTable}";
        private bool _committed;
        private long _staged;

        internal async Task CreateStagingTableAsync(CancellationToken cancellationToken)
        {
            // Unlogged temporary table holding only the mapped columns: no identity column,
            // no indexes, no WAL. Dropped when the transaction ends either way.
            var columns = string.Join(
                ", ",
                map.Columns.Select(c => $"{Quote(c.Name)} {TypeNameFor(c)}"));

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                $"create temporary table {Quote(_staging)} ({columns}) on commit drop";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteAsync(IReadOnlyList<TRecord> batch, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(batch);
            if (batch.Count == 0)
            {
                return;
            }

            var columnList = string.Join(", ", map.Columns.Select(c => Quote(c.Name)));
            var copySql = $"copy {Quote(_staging)} ({columnList}) from stdin (format binary)";

            await using var importer = await connection
                .BeginBinaryImportAsync(copySql, cancellationToken)
                .ConfigureAwait(false);

            foreach (var record in batch)
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await map.WriteRowAsync(importer, record, cancellationToken).ConfigureAwait(false);
            }

            await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            _staged += batch.Count;
        }

        public async Task<long> CommitAsync(CancellationToken cancellationToken)
        {
            var affected = await MergeAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _committed = true;

            logger.LogDebug(
                "Merged {Staged:N0} staged row(s) into {Table}, {Affected:N0} inserted or updated",
                _staged,
                map.TargetTable,
                affected);

            return affected;
        }

        /// <summary>
        /// One statement from staging into the target. Non-key columns are overwritten on
        /// conflict so re-ingesting a corrected file updates rows rather than duplicating
        /// or skipping them.
        /// </summary>
        private async Task<long> MergeAsync(CancellationToken cancellationToken)
        {
            var keys = map.Columns.Where(c => c.IsKey).Select(c => c.Name).ToArray();
            if (keys.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Table map for '{map.TargetTable}' declares no key columns, so an upsert has nothing to conflict on.");
            }

            var allColumns = string.Join(", ", map.Columns.Select(c => Quote(c.Name)));
            var conflictColumns = string.Join(", ", keys.Select(Quote));
            var updates = map.Columns
                .Where(c => !c.IsKey)
                .Select(c => $"{Quote(c.Name)} = excluded.{Quote(c.Name)}")
                .ToArray();

            var onConflict = updates.Length == 0
                ? "do nothing"
                : $"do update set {string.Join(", ", updates)}";

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                insert into {Quote(map.TargetTable)} ({allColumns})
                select {allColumns} from {Quote(_staging)}
                on conflict ({conflictColumns}) {onConflict}
                """;

            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                // Nothing staged becomes visible: the temporary table and every row in it
                // disappear with the transaction.
                await transaction.RollbackAsync().ConfigureAwait(false);
                logger.LogWarning(
                    "Rolled back {Staged:N0} staged row(s) for {Table}", _staged, map.TargetTable);
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        private static string Quote(string identifier) =>
            $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

        private static string TypeNameFor(PostgresColumn column) => column.DbType switch
        {
            NpgsqlTypes.NpgsqlDbType.Text => "text",
            NpgsqlTypes.NpgsqlDbType.Integer => "integer",
            NpgsqlTypes.NpgsqlDbType.Bigint => "bigint",
            NpgsqlTypes.NpgsqlDbType.Smallint => "smallint",
            NpgsqlTypes.NpgsqlDbType.Numeric => "numeric(20,8)",
            NpgsqlTypes.NpgsqlDbType.Double => "double precision",
            NpgsqlTypes.NpgsqlDbType.Boolean => "boolean",
            NpgsqlTypes.NpgsqlDbType.Date => "date",
            NpgsqlTypes.NpgsqlDbType.TimestampTz => "timestamptz",
            _ => throw new NotSupportedException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Column '{column.Name}' uses {column.DbType}, which the staging-table builder does not map to a Postgres type yet.")),
        };
    }
}
