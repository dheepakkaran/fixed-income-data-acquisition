namespace DataAcquisition.Reports.Model;

/// <summary>
/// Provenance for one ingestion that fed the report.
/// <para>
/// Written with init properties rather than as a positional record on purpose: Dapper
/// requires an exact constructor signature for a positional record and will not convert,
/// and Npgsql surfaces <c>timestamptz</c> as <see cref="DateTime"/> while this models it as
/// a <see cref="DateTimeOffset"/>. Init properties let Dapper convert.
/// </para>
/// </summary>
public sealed record RunProvenance
{
    /// <summary><c>ingestion_runs.id</c>, cited on the page so a number can be traced to its file.</summary>
    public long RunId { get; init; }

    /// <summary>Provider that produced it.</summary>
    public required string Provider { get; init; }

    /// <summary>Trust CIK, or <c>UST</c> for the yield curve.</summary>
    public string? SubjectId { get; init; }

    /// <summary>Period the source covered.</summary>
    public DateOnly? ReportingPeriod { get; init; }

    /// <summary>When the run finished; the freshness stamp.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Rows the run wrote.</summary>
    public long RecordsLoaded { get; init; }

    /// <summary>Content hash of the raw file.</summary>
    public string? Sha256 { get; init; }
}

/// <summary>One trust and its periods, newest first.</summary>
/// <param name="Cik">Trust CIK.</param>
/// <param name="Name">Display name from configuration.</param>
/// <param name="CreditTier">Credit tier from configuration, shown to explain why pools differ.</param>
/// <param name="Periods">Metrics per period, oldest first so charts read left to right.</param>
public sealed record TrustReport(
    string Cik,
    string Name,
    string CreditTier,
    IReadOnlyList<PoolPeriodMetrics> Periods)
{
    /// <summary>Most recent period, which the summary table shows.</summary>
    public PoolPeriodMetrics? Latest => Periods.Count == 0 ? null : Periods[^1];
}

/// <summary>Everything the report renders.</summary>
/// <param name="GeneratedAt">When this report was produced.</param>
/// <param name="Trusts">One entry per configured trust that has data.</param>
/// <param name="Provenance">Ingestion runs behind the figures.</param>
/// <param name="TreasuryAsOf">Latest Treasury curve date held.</param>
public sealed record ReportData(
    DateTimeOffset GeneratedAt,
    IReadOnlyList<TrustReport> Trusts,
    IReadOnlyList<RunProvenance> Provenance,
    DateOnly? TreasuryAsOf);
