using DataAcquisition.Providers.TreasuryYields.Model;
using DataAcquisition.Storage.Loading;
using Npgsql;
using NpgsqlTypes;

namespace DataAcquisition.Providers.TreasuryYields.Storage;

/// <summary>
/// Maps <see cref="TreasuryParYield"/> onto <c>treasury_par_yields</c>, created by migration
/// <c>0003_treasury_par_yields.sql</c>.
/// </summary>
public sealed class TreasuryParYieldTableMap : IPostgresTableMap<TreasuryParYield>
{
    public string TargetTable => "treasury_par_yields";

    public IReadOnlyList<PostgresColumn> Columns { get; } =
    [
        new("quote_date", NpgsqlDbType.Date, IsKey: true),
        new("tenor_months", NpgsqlDbType.Numeric, IsKey: true),
        new("tenor_label", NpgsqlDbType.Text),
        new("rate_percent", NpgsqlDbType.Numeric),
    ];

    public async ValueTask WriteRowAsync(
        NpgsqlBinaryImporter importer,
        TreasuryParYield record,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        await importer.WriteAsync(record.QuoteDate, NpgsqlDbType.Date, cancellationToken);
        await importer.WriteAsync(record.TenorMonths, NpgsqlDbType.Numeric, cancellationToken);
        await importer.WriteAsync(record.TenorLabel, NpgsqlDbType.Text, cancellationToken);
        await importer.WriteAsync(record.RatePercent, NpgsqlDbType.Numeric, cancellationToken);
    }

    public object?[] ToValues(TreasuryParYield record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return [record.QuoteDate, record.TenorMonths, record.TenorLabel, record.RatePercent];
    }
}
