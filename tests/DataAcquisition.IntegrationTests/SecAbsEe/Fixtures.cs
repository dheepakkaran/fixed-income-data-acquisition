namespace DataAcquisition.IntegrationTests.SecAbsEe;

/// <summary>Real sample files trimmed from exhibits downloaded from EDGAR.</summary>
public static class Fixtures
{
    private static string Root => Path.Combine(AppContext.BaseDirectory, "fixtures", "sec-abs-ee");

    public static string Gmcar => Path.Combine(Root, "gmcar-2026-08-200.xml");

    public static string Exeter => Path.Combine(Root, "eart-2026-08-200.xml");
}
