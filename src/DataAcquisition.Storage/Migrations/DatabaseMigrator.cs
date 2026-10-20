using System.Reflection;
using DbUp;
using DbUp.Engine;
using Microsoft.Extensions.Logging;

namespace DataAcquisition.Storage.Migrations;

/// <summary>
/// Applies the versioned SQL scripts in <c>db/migrations</c>, in filename order, recording
/// each in DbUp's journal table. The same code path runs locally, in CI and in the
/// production run, so the three never drift.
/// </summary>
public sealed class DatabaseMigrator(ILogger<DatabaseMigrator> logger)
{
    /// <summary>Prefix of the embedded migration resources.</summary>
    private const string ResourcePrefix = "DataAcquisition.Storage.Migrations.";

    private readonly ILogger<DatabaseMigrator> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Migration script names this assembly carries, in the order they apply.</summary>
    public static IReadOnlyList<string> ScriptNames =>
        [.. typeof(DatabaseMigrator).Assembly
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Create the database if it is absent, then apply every pending script.
    /// Safe to call on every start: applied scripts are skipped.
    /// </summary>
    public DatabaseUpgradeResult Upgrade(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        EnsureDatabase.For.PostgresqlDatabase(connectionString);

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogTo(new LoggerUpgradeLog(_logger))
            .Build();

        var pending = upgrader.GetScriptsToExecute().Select(s => s.Name).ToArray();
        if (pending.Length == 0)
        {
            _logger.LogInformation("Database schema is current; no migrations pending");
            return upgrader.PerformUpgrade();
        }

        _logger.LogInformation(
            "Applying {Count} migration(s): {Scripts}", pending.Length, string.Join(", ", pending));

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            _logger.LogError(result.Error, "Migration failed on {Script}", result.ErrorScript?.Name);
        }

        return result;
    }
}
