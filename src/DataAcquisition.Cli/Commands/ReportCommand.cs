using DataAcquisition.Providers.SecAbsEe;
using DataAcquisition.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DataAcquisition.Cli.Commands;

/// <summary>Generates the static ABS pool performance report.</summary>
public static class ReportCommand
{
    public static async Task<int> RunAsync(
        IServiceProvider services,
        ParsedArgs args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);

        var generator = services.GetRequiredService<ReportGenerator>();

        // Trust names and credit tiers come from the SEC provider's configuration, which is
        // the component that knows what a trust is; the report itself only knows CIKs.
        var secOptions = services.GetRequiredService<IOptions<SecAbsEeOptions>>().Value;
        var trusts = secOptions.Trusts
            .Select(t => new TrustDescriptor(
                EdgarClientCik(t.Cik),
                string.IsNullOrWhiteSpace(t.Name) ? $"CIK {t.Cik}" : t.Name,
                t.CreditTier))
            .ToArray();

        var data = await generator.BuildAsync(trusts, cancellationToken).ConfigureAwait(false);

        if (data.Trusts.Count == 0)
        {
            Console.Error.WriteLine(
                "No ingested pool data found. Run `ingest` before `report`.");
            return 1;
        }

        var output = args.Value("out") ?? "./site";
        var written = await generator.WriteAsync(data, output, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"Report written to {Path.GetFullPath(output)}");
        foreach (var path in written)
        {
            var size = new FileInfo(path).Length;
            Console.WriteLine($"  {Path.GetFileName(path),-40} {size,9:N0} bytes");
        }

        Console.WriteLine();
        foreach (var trust in data.Trusts)
        {
            var latest = trust.Latest;
            Console.WriteLine(
                $"  {trust.Name,-58} {trust.Periods.Count} period(s), latest {latest?.ReportingPeriodEnd:yyyy-MM}");
        }

        return 0;
    }

    /// <summary>Trust CIKs are stored without leading zeros, matching the Archives path form.</summary>
    private static string EdgarClientCik(string cik) =>
        Providers.SecAbsEe.Http.EdgarClient.TrimCik(cik);
}
