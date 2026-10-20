using Npgsql;
using Testcontainers.PostgreSql;

namespace DataAcquisition.IntegrationTests.Infrastructure;

/// <summary>
/// A real PostgreSQL 16 in a container, the same engine the production run uses. COPY,
/// upserts, partial indexes, check constraints and transaction rollback cannot be trusted
/// against an in-memory fake, which is the whole reason this fixture exists.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("fida_test")
        .WithUsername("fida")
        .WithPassword("fida_test")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
        DataSource = new NpgsqlDataSourceBuilder(ConnectionString).Build();
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }

    /// <summary>Drop everything in the public schema so a test starts from an empty database.</summary>
    public async Task ResetAsync()
    {
        await using var connection = await DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "drop schema public cascade; create schema public;";
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Shares one container across every test class in the collection.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
