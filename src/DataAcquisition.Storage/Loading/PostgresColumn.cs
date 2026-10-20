using NpgsqlTypes;

namespace DataAcquisition.Storage.Loading;

/// <summary>One mapped column of a target table.</summary>
/// <param name="Name">Column name, as written in the migration.</param>
/// <param name="DbType">
/// Postgres type, needed because binary COPY writes values in the column's wire format
/// rather than letting the server infer one.
/// </param>
/// <param name="IsKey">Part of the natural key the upsert conflicts on.</param>
public readonly record struct PostgresColumn(string Name, NpgsqlDbType DbType, bool IsKey = false);
