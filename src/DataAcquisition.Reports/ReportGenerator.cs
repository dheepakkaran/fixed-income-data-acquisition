using DataAcquisition.Reports.Model;
using DataAcquisition.Reports.Rendering;
using Microsoft.Extensions.Logging;

namespace DataAcquisition.Reports;

/// <summary>Names a trust for the report. Supplied by whichever provider knows about trusts.</summary>
/// <param name="Cik">Trust CIK, matching <c>auto_loan_asset_data.trust_cik</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="CreditTier">Credit tier, shown to explain why the pools differ so much.</param>
public sealed record TrustDescriptor(string Cik, string Name, string CreditTier);

/// <summary>Builds the report and writes it to a directory ready for static hosting.</summary>
public sealed class ReportGenerator(
    PoolReportRepository repository,
    ILogger<ReportGenerator> logger)
{
    private readonly PoolReportRepository _repository =
        repository ?? throw new ArgumentNullException(nameof(repository));

    private readonly ILogger<ReportGenerator> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Assemble the report's data from the database.</summary>
    public async Task<ReportData> BuildAsync(
        IReadOnlyList<TrustDescriptor> trusts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(trusts);

        var metrics = await _repository.GetMetricsAsync(cancellationToken).ConfigureAwait(false);
        var provenance = await _repository.GetProvenanceAsync(cancellationToken).ConfigureAwait(false);
        var treasuryAsOf = await _repository.GetTreasuryAsOfAsync(cancellationToken).ConfigureAwait(false);

        var byTrust = metrics
            .GroupBy(m => m.TrustCik, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<PoolPeriodMetrics>)[.. g.OrderBy(m => m.ReportingPeriodEnd)],
                StringComparer.Ordinal);

        var reports = new List<TrustReport>();

        // Configured trusts first, in configuration order, so the report is stable.
        foreach (var trust in trusts)
        {
            if (byTrust.TryGetValue(trust.Cik, out var periods))
            {
                reports.Add(new TrustReport(trust.Cik, trust.Name, trust.CreditTier, periods));
                byTrust.Remove(trust.Cik);
            }
            else
            {
                _logger.LogWarning(
                    "Trust {Cik} ({Name}) is configured but has no ingested data", trust.Cik, trust.Name);
            }
        }

        // Anything in the database but not configured still gets reported, rather than
        // silently vanishing from a report that claims to cover the data.
        foreach (var (cik, periods) in byTrust.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            _logger.LogWarning("Trust {Cik} has ingested data but no configuration entry", cik);
            reports.Add(new TrustReport(cik, $"CIK {cik}", "Unconfigured", periods));
        }

        return new ReportData(DateTimeOffset.UtcNow, reports, provenance, treasuryAsOf);
    }

    /// <summary>
    /// Write <c>index.html</c> and one CSV per trust into <paramref name="outputDirectory"/>.
    /// </summary>
    /// <returns>Paths written, for the CLI to report.</returns>
    public async Task<IReadOnlyList<string>> WriteAsync(
        ReportData data,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        Directory.CreateDirectory(outputDirectory);
        var written = new List<string>();

        var indexPath = Path.Combine(outputDirectory, "index.html");
        await File.WriteAllTextAsync(indexPath, HtmlReportWriter.Render(data), cancellationToken)
            .ConfigureAwait(false);
        written.Add(indexPath);

        foreach (var trust in data.Trusts)
        {
            var csvPath = Path.Combine(outputDirectory, CsvReportWriter.FileNameFor(trust));
            await File.WriteAllTextAsync(csvPath, CsvReportWriter.Render(trust), cancellationToken)
                .ConfigureAwait(false);
            written.Add(csvPath);
        }

        _logger.LogInformation(
            "Wrote {Count} file(s) to {Directory} covering {Trusts} trust(s)",
            written.Count,
            outputDirectory,
            data.Trusts.Count);

        return written;
    }
}
