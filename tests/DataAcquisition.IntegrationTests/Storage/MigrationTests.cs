using Dapper;
using DataAcquisition.IntegrationTests.Infrastructure;
using DataAcquisition.Storage.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataAcquisition.IntegrationTests.Storage;

[Collection(PostgresCollection.Name)]
public sealed class MigrationTests(PostgresFixture postgres)
{
    private static DatabaseMigrator Migrator() => new(NullLogger<DatabaseMigrator>.Instance);

    [Fact]
    public async Task Migrations_apply_cleanly_on_an_empty_database()
    {
        await postgres.ResetAsync();

        var result = Migrator().Upgrade(postgres.ConnectionString);

        Assert.True(result.Successful, result.Error?.ToString());
        Assert.NotEmpty(DatabaseMigrator.ScriptNames);

        await using var connection = await postgres.DataSource.OpenConnectionAsync();

        var tables = (await connection.QueryAsync<string>(
            "select table_name from information_schema.tables where table_schema = 'public'"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ingestion_runs", tables);
        Assert.Contains("source_files", tables);

        // The partial index the already-ingested check relies on must exist, or that check
        // silently degrades to a sequential scan on every source.
        var indexes = (await connection.QueryAsync<string>(
            "select indexname from pg_indexes where schemaname = 'public'"))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("ix_ingestion_runs_identity", indexes);
        Assert.Contains("ix_ingestion_runs_subject_period", indexes);
    }

    [Fact]
    public async Task Applying_migrations_twice_is_a_no_op()
    {
        await postgres.ResetAsync();

        Assert.True(Migrator().Upgrade(postgres.ConnectionString).Successful);
        var second = Migrator().Upgrade(postgres.ConnectionString);

        Assert.True(second.Successful, second.Error?.ToString());
        Assert.Empty(second.Scripts);
    }

    [Fact]
    public async Task Status_check_constraint_rejects_an_unknown_status()
    {
        await postgres.ResetAsync();
        Assert.True(Migrator().Upgrade(postgres.ConnectionString).Successful);

        await using var connection = await postgres.DataSource.OpenConnectionAsync();

        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
            connection.ExecuteAsync("""
                insert into ingestion_runs
                    (provider_name, asset_class, source_id, status, started_at)
                values ('P', 'A', 'S', 99, now())
                """));

        Assert.Contains("ingestion_runs_status_known", ex.Message, StringComparison.Ordinal);
    }
}
