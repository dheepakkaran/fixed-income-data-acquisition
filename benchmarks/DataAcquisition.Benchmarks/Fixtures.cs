namespace DataAcquisition.Benchmarks;

/// <summary>Locates the real sample files under <c>tests/fixtures</c> from the build output.</summary>
public static class Fixtures
{
    /// <summary>Absolute path to a fixture, searching upward for the repository root.</summary>
    public static string Path(params string[] parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(
                [directory.FullName, "tests", "fixtures", .. parts]);

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Fixture '{string.Join('/', parts)}' not found above {AppContext.BaseDirectory}.");
    }
}
