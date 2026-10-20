namespace DataAcquisition.Providers.SecAbsEe;

/// <summary>Configuration for the SEC EDGAR ABS-EE provider.</summary>
public sealed class SecAbsEeOptions
{
    public const string SectionName = "Providers:SecAbsEe";

    /// <summary>Whether the registry runs this provider.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Declared to SEC on every request, as fair access requires: a real name and a
    /// reachable email. Overridable by <c>SEC_USER_AGENT</c> so a fork does not file
    /// requests under someone else's name.
    /// </summary>
    public string UserAgent { get; set; } =
        "Dheepak Karan Elumalai Santhakumari elumalaisanthakuma.d@northeastern.edu";

    /// <summary>Hard ceiling on request rate. SEC fair access allows no more than 10/second.</summary>
    public int MaxRequestsPerSecond { get; set; } = 10;

    /// <summary>Trust CIKs to ingest, in configuration rather than code so adding a trust is not a code change.</summary>
    public IList<TrustOptions> Trusts { get; set; } = [];

    /// <summary>Most recent reporting periods to ingest per trust.</summary>
    public int MaxPeriods { get; set; } = 3;

    /// <summary>Refuse an exhibit larger than this, so one unexpected filing cannot fill the disk.</summary>
    public long MaxExhibitBytes { get; set; } = 400L * 1024 * 1024;
}

/// <summary>One securitisation trust that files Form ABS-EE.</summary>
public sealed class TrustOptions
{
    /// <summary>Central Index Key, zero-padded or not; both forms are accepted.</summary>
    public string Cik { get; set; } = string.Empty;

    /// <summary>Display name used in the report.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Credit tier, shown in the report to explain why pools differ so much.</summary>
    public string CreditTier { get; set; } = string.Empty;
}
