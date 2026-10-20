using DataAcquisition.Storage;
using DataAcquisition.Storage.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DataAcquisition.Cli.Commands;

/// <summary>Applies pending migrations and reports what ran.</summary>
public static class MigrateCommand
{
    public static Task<int> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        cancellationToken.ThrowIfCancellationRequested();

        var migrator = services.GetRequiredService<DatabaseMigrator>();
        var options = services.GetRequiredService<IOptions<StorageOptions>>().Value;

        var result = migrator.Upgrade(options.ConnectionString);
        if (!result.Successful)
        {
            Console.Error.WriteLine($"Migration failed: {result.Error?.Message}");
            return Task.FromResult(1);
        }

        Console.WriteLine(!result.Scripts.Any()
            ? "Schema already current."
            : $"Applied {result.Scripts.Count()} migration(s): {string.Join(", ", result.Scripts.Select(s => s.Name))}");

        return Task.FromResult(0);
    }
}
