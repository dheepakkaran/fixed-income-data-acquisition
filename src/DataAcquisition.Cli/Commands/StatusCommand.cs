using DataAcquisition.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace DataAcquisition.Cli.Commands;

/// <summary>Prints recent ingestion runs, newest first.</summary>
public static class StatusCommand
{
    public static async Task<int> RunAsync(
        IServiceProvider services,
        ParsedArgs args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        var store = services.GetRequiredService<IIngestionRunStore>();
        var runs = await store
            .GetRunsAsync(args.Value("provider"), args.Int("limit") ?? 20, cancellationToken)
            .ConfigureAwait(false);

        if (runs.Count == 0)
        {
            Console.WriteLine("No ingestion runs recorded.");
            return 0;
        }

        Console.WriteLine($"{"id",6}  {"provider",-14} {"subject",-9} {"period",-8} {"status",-9} "
                        + $"{"parsed",9} {"loaded",9} {"warn",6}  started");
        Console.WriteLine(new string('-', 104));

        foreach (var run in runs)
        {
            Console.WriteLine(
                $"{run.Id,6}  {run.ProviderName,-14} {run.SubjectId,-9} "
                + $"{run.ReportingPeriod?.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture) ?? "-",-8} {run.Status,-9} "
                + $"{run.RecordsParsed,9:N0} {run.RecordsLoaded,9:N0} {run.ValidationWarnings,6:N0}  "
                + $"{run.StartedAt:yyyy-MM-dd HH:mm:ss}"
                + (run.ErrorMessage is { } e ? $"  {e}" : string.Empty));
        }

        return 0;
    }
}
