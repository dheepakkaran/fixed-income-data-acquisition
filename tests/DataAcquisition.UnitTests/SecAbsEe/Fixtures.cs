namespace DataAcquisition.UnitTests.SecAbsEe;

/// <summary>
/// Paths to the real sample files under <c>tests/fixtures</c>, trimmed from exhibits
/// actually downloaded from EDGAR rather than hand-written.
/// </summary>
public static class Fixtures
{
    private static string Root => Path.Combine(AppContext.BaseDirectory, "fixtures", "sec-abs-ee");

    /// <summary>
    /// GM Financial Consumer Automobile Receivables Trust 2026-1, period 2026-08-31,
    /// first 200 of 29,152 records. Pretty-printed across 14,721 lines.
    /// </summary>
    public static string Gmcar => Path.Combine(Root, "gmcar-2026-08-200.xml");

    /// <summary>
    /// Exeter Automobile Receivables Trust 2024-2, period 2026-08-31, first 200 of 38,648
    /// records. The source exhibit is a single 144 MB line with no newline anywhere, which
    /// is the case a line-oriented parser gets wrong.
    /// </summary>
    public static string Exeter => Path.Combine(Root, "eart-2026-08-200.xml");
}
