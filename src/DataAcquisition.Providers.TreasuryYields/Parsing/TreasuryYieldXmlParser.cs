using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using DataAcquisition.Providers.TreasuryYields.Model;

namespace DataAcquisition.Providers.TreasuryYields.Parsing;

/// <summary>
/// Streams <see cref="TreasuryParYield"/> points out of Treasury's daily par yield curve
/// feed, an Atom document whose payload sits in Microsoft ADO.NET data-services elements.
/// </summary>
public static class TreasuryYieldXmlParser
{
    /// <summary>Namespace of the <c>d:</c> payload elements inside each entry.</summary>
    public const string DataServicesNamespace =
        "http://schemas.microsoft.com/ado/2007/08/dataservices";

    /// <summary>
    /// Feed field name to tenor. Kept as an explicit table rather than derived from the
    /// field name, because the names do not parse uniformly (<c>BC_1_5MONTH</c> is 1.5
    /// months) and would not sort into curve order as text.
    /// <para>
    /// <c>BC_30YEARDISPLAY</c> is deliberately absent: it duplicates <c>BC_30YEAR</c>, and
    /// including it would put two rows at the same tenor and double-count the long end.
    /// </para>
    /// </summary>
    private static readonly (string Field, decimal Months, string Label)[] Tenors =
    [
        ("BC_1MONTH", 1m, "1M"),
        ("BC_1_5MONTH", 1.5m, "1.5M"),
        ("BC_2MONTH", 2m, "2M"),
        ("BC_3MONTH", 3m, "3M"),
        ("BC_4MONTH", 4m, "4M"),
        ("BC_6MONTH", 6m, "6M"),
        ("BC_1YEAR", 12m, "1Y"),
        ("BC_2YEAR", 24m, "2Y"),
        ("BC_3YEAR", 36m, "3Y"),
        ("BC_5YEAR", 60m, "5Y"),
        ("BC_7YEAR", 84m, "7Y"),
        ("BC_10YEAR", 120m, "10Y"),
        ("BC_20YEAR", 240m, "20Y"),
        ("BC_30YEAR", 360m, "30Y"),
    ];

    /// <summary>The field this feed publishes that is a duplicate of another tenor.</summary>
    public const string DuplicateField = "BC_30YEARDISPLAY";

    /// <summary>Tenors the parser emits, in curve order.</summary>
    public static IReadOnlyList<(string Field, decimal Months, string Label)> KnownTenors => Tenors;

    private static readonly Dictionary<string, (decimal Months, string Label)> ByField =
        Tenors.ToDictionary(t => t.Field, t => (t.Months, t.Label), StringComparer.Ordinal);

    private static XmlReaderSettings Settings() => new()
    {
        Async = true,
        IgnoreWhitespace = true,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        CloseInput = false,
    };

    /// <summary>Parse a feed from disk.</summary>
    public static IAsyncEnumerable<TreasuryParYield> ParseFileAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);

        return ParseAsync(stream, ownsStream: true, cancellationToken);
    }

    /// <summary>
    /// Parse from an open stream. One Atom <c>entry</c> yields up to fourteen points, one
    /// per tenor; a tenor the feed leaves blank for that day is skipped rather than stored
    /// as zero, because a missing quote is not a yield of zero.
    /// </summary>
    public static async IAsyncEnumerable<TreasuryParYield> ParseAsync(
        Stream stream,
        bool ownsStream = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            using var reader = XmlReader.Create(stream, Settings());

            DateOnly? quoteDate = null;
            var points = new List<TreasuryParYield>(Tenors.Length);

            // ReadElementContentAsStringAsync leaves the reader on the NEXT node, so this
            // loop must not call ReadAsync after it. Advancing again skipped every other
            // tenor: a year that holds 200 business days x 14 tenors = 2,800 points came
            // back as 1,400. Only the branch that consumes nothing advances the reader.
            var advance = true;
            while (true)
            {
                if (advance && !await reader.ReadAsync().ConfigureAwait(false))
                {
                    break;
                }

                advance = true;
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.NodeType == XmlNodeType.EndElement
                    && string.Equals(reader.LocalName, "properties", StringComparison.Ordinal))
                {
                    foreach (var point in points)
                    {
                        yield return point;
                    }

                    points.Clear();
                    quoteDate = null;
                    continue;
                }

                if (reader.NodeType != XmlNodeType.Element
                    || !string.Equals(reader.NamespaceURI, DataServicesNamespace, StringComparison.Ordinal))
                {
                    continue;
                }

                var name = reader.LocalName;

                if (string.Equals(name, "NEW_DATE", StringComparison.Ordinal))
                {
                    var raw = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    quoteDate = ParseQuoteDate(raw);
                    advance = false;
                    continue;
                }

                if (!ByField.TryGetValue(name, out var tenor))
                {
                    // Includes BC_30YEARDISPLAY, which duplicates BC_30YEAR, and the feed's
                    // own Id element.
                    continue;
                }

                var value = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                advance = false;

                if (quoteDate is { } date && ParseRate(value) is { } rate)
                {
                    points.Add(new TreasuryParYield(date, tenor.Months, tenor.Label, rate));
                }
            }

            // A feed whose last entry is unterminated still yields what was read.
            foreach (var point in points)
            {
                yield return point;
            }
        }
        finally
        {
            if (ownsStream)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary><c>d:NEW_DATE</c> is an <c>Edm.DateTime</c> such as <c>2026-01-02T00:00:00</c>.</summary>
    internal static DateOnly? ParseQuoteDate(string value) =>
        DateTime.TryParse(
            value.Trim(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? DateOnly.FromDateTime(parsed)
            : null;

    /// <summary>A blank tenor means no quote that day, which is null rather than zero.</summary>
    internal static decimal? ParseRate(string value) =>
        decimal.TryParse(
            value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
