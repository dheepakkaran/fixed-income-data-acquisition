using Npgsql;

namespace DataAcquisition.Storage.Loading;

/// <summary>
/// How one record type maps to one Postgres table. A provider supplies this alongside its
/// migration; the loaders are generic over it and contain no provider-specific SQL.
/// </summary>
/// <typeparam name="TRecord">Record type being mapped.</typeparam>
public interface IPostgresTableMap<TRecord>
{
    /// <summary>Unqualified target table name, as created by the provider's migration.</summary>
    string TargetTable { get; }

    /// <summary>Mapped columns in wire order. <see cref="WriteRowAsync"/> writes exactly these, in this order.</summary>
    IReadOnlyList<PostgresColumn> Columns { get; }

    /// <summary>
    /// Write one record's values to an open binary COPY stream. The caller has already
    /// started the row; this writes <see cref="Columns"/>.Count values in order.
    /// </summary>
    ValueTask WriteRowAsync(NpgsqlBinaryImporter importer, TRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Record values boxed in <see cref="Columns"/> order, for the row-by-row INSERT
    /// baseline. Boxing is acceptable there: that path exists to be measured against
    /// COPY, not to be fast.
    /// </summary>
    object?[] ToValues(TRecord record);
}
