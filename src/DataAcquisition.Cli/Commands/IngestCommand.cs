using DataAcquisition.Core.Model;
using DataAcquisition.Core.Registry;
using Microsoft.Extensions.DependencyInjection;

namespace DataAcquisition.Cli.Commands;

/// <summary>Runs the enabled providers and prints a per-source summary.</summary>
public static class IngestCommand
{
    public static async Task<int> RunAsync(
        IServiceProvider services,
        ParsedArgs args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        var registry = services.GetRequiredService<ProviderRegistry>();

        var providerName = args.Value("provider");
        var providers = providerName is null
            ? registry.Enabled
            : [registry.Get(providerName)];

        if (providers.Count == 0)
        {
            Console.Error.WriteLine("No providers are enabled.");
            return 1;
        }

        var options = new IngestionOptions
        {
            MaxPeriods = args.Int("periods") ?? 3,
            Force = args.Flag("force"),
            DryRun = args.Flag("dry-run"),
            Subjects = args.Value("subject")?.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                      | StringSplitOptions.TrimEntries) ?? [],
        };

        var failed = 0;
        foreach (var provider in providers)
        {
            var summary = await provider.IngestAsync(options, cancellationToken).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"{provider.Name} ({provider.AssetClass})");
            Console.WriteLine(new string('-', 78));
            foreach (var run in summary.Runs)
            {
                Console.WriteLine(
                    $"  {run.Status,-9} {run.SubjectId,-10} {run.ReportingPeriod:yyyy-MM}  "
                    + $"parsed {run.RecordsParsed,8:N0}  loaded {run.RecordsLoaded,8:N0}"
                    + (run.Duration is { } d ? $"  {d.TotalSeconds,6:N1}s" : string.Empty)
                    + (run.ErrorMessage is { } e ? $"  {e}" : string.Empty));
            }

            Console.WriteLine(
                $"  => {summary.Succeeded} succeeded, {summary.Skipped} skipped, {summary.Failed} failed, "
                + $"{summary.RecordsLoaded:N0} record(s) loaded");

            failed += summary.Failed;
        }

        // Fail loudly: the production run must go red on a validation failure.
        return failed == 0 ? 0 : 1;
    }
}
