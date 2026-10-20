namespace DataAcquisition.Providers.TreasuryYields;

/// <summary>Configuration for the U.S. Treasury par yield curve provider.</summary>
public sealed class TreasuryYieldsOptions
{
    public const string SectionName = "Providers:TreasuryYields";

    public bool Enabled { get; set; } = true;

    /// <summary>Identifies this client to Treasury. Overridable by <c>TREASURY_USER_AGENT</c>.</summary>
    public string UserAgent { get; set; } =
        "Dheepak Karan Elumalai Santhakumari elumalaisanthakuma.d@northeastern.edu";

    /// <summary>
    /// Calendar years to ingest, counting back from the current year. The feed is published
    /// one year per request, so this is also the request count per run.
    /// </summary>
    public int Years { get; set; } = 2;
}
