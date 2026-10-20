using DataAcquisition.Storage.Loading;
using Npgsql;
using NpgsqlTypes;

namespace DataAcquisition.IntegrationTests.Fakes;

/// <summary>Maps <see cref="WidgetRow"/> onto the <c>widgets</c> test table.</summary>
public sealed class WidgetTableMap : IPostgresTableMap<WidgetRow>
{
    /// <summary>DDL for the target table, including the unique key the upsert conflicts on.</summary>
    public const string CreateTableSql = """
        create table if not exists widgets (
            trust     text           not null,
            period    date           not null,
            asset_id  text           not null,
            balance   numeric(20,8),
            score     integer,
            primary key (trust, period, asset_id)
        )
        """;

    public string TargetTable => "widgets";

    public IReadOnlyList<PostgresColumn> Columns { get; } =
    [
        new("trust", NpgsqlDbType.Text, IsKey: true),
        new("period", NpgsqlDbType.Date, IsKey: true),
        new("asset_id", NpgsqlDbType.Text, IsKey: true),
        new("balance", NpgsqlDbType.Numeric),
        new("score", NpgsqlDbType.Integer),
    ];

    public async ValueTask WriteRowAsync(
        NpgsqlBinaryImporter importer,
        WidgetRow record,
        CancellationToken cancellationToken)
    {
        await importer.WriteAsync(record.Trust, NpgsqlDbType.Text, cancellationToken);
        await importer.WriteAsync(record.Period, NpgsqlDbType.Date, cancellationToken);
        await importer.WriteAsync(record.AssetId, NpgsqlDbType.Text, cancellationToken);

        if (record.Balance is { } balance)
        {
            await importer.WriteAsync(balance, NpgsqlDbType.Numeric, cancellationToken);
        }
        else
        {
            await importer.WriteNullAsync(cancellationToken);
        }

        if (record.Score is { } score)
        {
            await importer.WriteAsync(score, NpgsqlDbType.Integer, cancellationToken);
        }
        else
        {
            await importer.WriteNullAsync(cancellationToken);
        }
    }

    public object?[] ToValues(WidgetRow record) =>
        [record.Trust, record.Period, record.AssetId, record.Balance, record.Score];
}
