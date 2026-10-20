using DataAcquisition.Core.Abstractions;
using DataAcquisition.Core.Model;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DataAcquisition.Storage.Loading;

/// <summary>
/// Row-by-row parameterised <c>INSERT ... ON CONFLICT DO UPDATE</c>, the obvious
/// implementation. It exists as the measured baseline for
/// <see cref="CopyBulkLoader{TRecord}"/>: RESULTS.md reports both on the same data, which
/// is what justifies COPY being in the stack at all. Not wired into the production run.
/// </summary>
/// <typeparam name="TRecord">Record type being written.</typeparam>
public sealed class InsertBulkLoader<TRecord>(
    NpgsqlDataSource dataSource,
    IPostgresTableMap<TRecord> map,
    ILogger<InsertBulkLoader<TRecord>> logger) : IBulkLoader<TRecord>
{
    private readonly NpgsqlDataSource _dataSource =
        dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    private readonly IPostgresTableMap<TRecord> _map = map ?? throw new ArgumentNullException(nameof(map));

    private readonly ILogger<InsertBulkLoader<TRecord>> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public string Strategy => "row-insert";

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

            return new Session(connection, transaction, _map, _logger);
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
        private readonly string _sql = BuildSql(map);
        private bool _committed;
        private long _written;

        private static string BuildSql(IPostgresTableMap<TRecord> map)
        {
            var keys = map.Columns.Where(c => c.IsKey).Select(c => c.Name).ToArray();
            if (keys.Length == 0)
            {
                throw new InvalidOperationException(
                    $"Table map for '{map.TargetTable}' declares no key columns.");
            }

            var columns = string.Join(", ", map.Columns.Select(c => Quote(c.Name)));
            var parameters = string.Join(", ", map.Columns.Select((_, i) => $"${i + 1}"));
            var updates = map.Columns
                .Where(c => !c.IsKey)
                .Select(c => $"{Quote(c.Name)} = excluded.{Quote(c.Name)}")
                .ToArray();

            var onConflict = updates.Length == 0
                ? "do nothing"
                : $"do update set {string.Join(", ", updates)}";

            return $"insert into {Quote(map.TargetTable)} ({columns}) values ({parameters}) "
                 + $"on conflict ({string.Join(", ", keys.Select(Quote))}) {onConflict}";
        }

        public async Task WriteAsync(IReadOnlyList<TRecord> batch, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(batch);

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = _sql;

            for (var i = 0; i < map.Columns.Count; i++)
            {
                command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = map.Columns[i].DbType });
            }

            await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

            foreach (var record in batch)
            {
                var values = map.ToValues(record);
                for (var i = 0; i < values.Length; i++)
                {
                    command.Parameters[i].Value = values[i] ?? DBNull.Value;
                }

                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                _written++;
            }
        }

        public async Task<long> CommitAsync(CancellationToken cancellationToken)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _committed = true;
            logger.LogDebug("Inserted {Written:N0} row(s) into {Table}", _written, map.TargetTable);
            return _written;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_committed)
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        private static string Quote(string identifier) =>
            $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
