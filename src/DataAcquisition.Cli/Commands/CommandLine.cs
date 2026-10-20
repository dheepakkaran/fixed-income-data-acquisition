using System.Globalization;

namespace DataAcquisition.Cli.Commands;

/// <summary>
/// Minimal argument parsing. A command-line library is not in the approved stack and three
/// commands with a handful of flags do not justify adding one.
/// </summary>
public sealed record ParsedArgs
{
    public string? Command { get; init; }

    public bool HelpRequested { get; init; }

    public Dictionary<string, string?> Options { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Flag(string name) => Options.ContainsKey(name);

    public string? Value(string name) => Options.TryGetValue(name, out var value) ? value : null;

    public int? Int(string name) =>
        int.TryParse(Value(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}

/// <summary>Parses <c>command --flag --option value</c>.</summary>
public static class CommandLine
{
    public static ParsedArgs Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return new ParsedArgs();
        }

        if (args[0] is "-h" or "--help" or "help")
        {
            return new ParsedArgs { HelpRequested = true };
        }

        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var name = args[i][2..];
            var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            options[name] = hasValue ? args[++i] : null;
        }

        return new ParsedArgs { Command = args[0], Options = options };
    }

    public static void PrintUsage() => Console.WriteLine(
        """
        fixed-income-data-acquisition

        Usage:
          migrate                              Apply pending database migrations.
          ingest  [options]                    Run the enabled providers.
          status  [options]                    Show recent ingestion runs.
          report  [options]                    Generate the static ABS pool performance report.

        ingest options:
          --provider <name>                    Run one provider instead of all enabled ones.
          --periods  <n>                       Reporting periods per subject (default 3).
          --subject  <id>                      Restrict to one subject (a trust CIK). Repeatable via commas.
          --force                              Re-ingest even when the content hash already succeeded.
          --dry-run                            Discover only: no fetch, no load.

        status options:
          --provider <name>                    Filter by provider.
          --limit    <n>                       Rows to show (default 20).

        report options:
          --out <dir>                          Output directory (default ./site).
        """);
}
