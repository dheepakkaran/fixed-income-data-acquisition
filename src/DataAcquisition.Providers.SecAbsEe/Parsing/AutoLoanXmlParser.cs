using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml;
using DataAcquisition.Providers.SecAbsEe.Model;

namespace DataAcquisition.Providers.SecAbsEe.Parsing;

/// <summary>
/// Streams <see cref="AutoLoanRecord"/>s out of an ABS-EE EX-102 asset data file with
/// <see cref="XmlReader"/>.
/// <para>
/// Streaming is not a micro-optimisation here. Exhibits run to 160 MB, and formatting is
/// entirely up to the filing agent: GM Financial's exhibit for 2026-08 is 2,138,467 lines
/// while Exeter's is a single 144 MB line with no newline at all. Anything that reads lines,
/// or loads the document, either exhausts memory or depends on whitespace the schema never
/// promised. <c>XmlReader</c> cares about neither.
/// </para>
/// </summary>
public static class AutoLoanXmlParser
{
    /// <summary>Namespace declared by every EX-102 auto loan asset data file.</summary>
    public const string Namespace = "http://www.sec.gov/edgar/document/absee/autoloan/assetdata";

    private const string AssetElement = "assets";

    /// <summary>
    /// Reader settings used for every parse. DTD processing is prohibited: these files are
    /// downloaded from the internet and a DTD is an external-entity vector, not something
    /// the ABS-EE schema needs.
    /// </summary>
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

    /// <summary>Parse a file from disk.</summary>
    public static IAsyncEnumerable<AutoLoanRecord> ParseFileAsync(
        string path,
        string trustCik,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);

        return ParseAsync(stream, trustCik, ownsStream: true, cancellationToken);
    }

    /// <summary>Parse from an open stream.</summary>
    public static async IAsyncEnumerable<AutoLoanRecord> ParseAsync(
        Stream stream,
        string trustCik,
        bool ownsStream = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustCik);

        try
        {
            using var reader = XmlReader.Create(stream, Settings());

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (reader.NodeType != XmlNodeType.Element
                    || !string.Equals(reader.LocalName, AssetElement, StringComparison.Ordinal))
                {
                    continue;
                }

                var record = await ReadAssetAsync(reader, trustCik).ConfigureAwait(false);
                if (record is not null)
                {
                    yield return record;
                }
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

    /// <summary>
    /// Read one <c>&lt;assets&gt;</c> element. Fields are matched by name rather than
    /// position: filers differ in which optional elements they emit at all (Exeter omits
    /// <c>servicerAdvancedAmount</c> and <c>servicingFlatFeeAmount</c> entirely), so
    /// anything order- or count-dependent would break between trusts.
    /// </summary>
    private static async Task<AutoLoanRecord?> ReadAssetAsync(XmlReader reader, string trustCik)
    {
        if (reader.IsEmptyElement)
        {
            return null;
        }

        var builder = new Builder { TrustCik = trustCik };

        // Move onto the first child of <assets>.
        if (!await reader.ReadAsync().ConfigureAwait(false))
        {
            return builder.Build();
        }

        // Both SkipAsync and ReadElementContentAsStringAsync leave the reader positioned on
        // the NEXT node, so this loop must not call ReadAsync after them: doing so skips
        // every other element. Only the branch that consumes nothing advances the reader.
        while (true)
        {
            if (reader.NodeType == XmlNodeType.EndElement
                && string.Equals(reader.LocalName, AssetElement, StringComparison.Ordinal))
            {
                break;
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                var name = reader.LocalName;
                if (IsWanted(name))
                {
                    var value = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                    builder.Apply(name, value);
                }
                else
                {
                    await reader.SkipAsync().ConfigureAwait(false);
                }

                continue;
            }

            if (!await reader.ReadAsync().ConfigureAwait(false))
            {
                break;
            }
        }

        return builder.Build();
    }

    /// <summary>
    /// Elements the report needs. Everything else is skipped without materialising its text,
    /// which is most of the 67 elements in the file.
    /// </summary>
    private static bool IsWanted(string name) => name switch
    {
        "assetNumber" or
        "reportingPeriodBeginningDate" or
        "reportingPeriodEndingDate" or
        "originalLoanAmount" or
        "originalLoanTerm" or
        "originalInterestRatePercentage" or
        "reportingPeriodInterestRatePercentage" or
        "obligorCreditScore" or
        "reportingPeriodBeginningLoanBalanceAmount" or
        "reportingPeriodActualEndBalanceAmount" or
        "remainingTermToMaturityNumber" or
        "currentDelinquencyStatus" or
        "chargedoffPrincipalAmount" or
        "recoveredAmount" or
        "zeroBalanceCode" or
        "zeroBalanceEffectiveDate" => true,
        _ => false,
    };

    /// <summary>Mutable accumulator for one record, so a parse allocates one object per loan.</summary>
    private sealed class Builder
    {
        public required string TrustCik { get; init; }

        private string? _assetNumber;
        private DateOnly? _periodBegin;
        private DateOnly? _periodEnd;
        private decimal? _originalAmount;
        private int? _originalTerm;
        private decimal? _originalRate;
        private decimal? _periodRate;
        private int? _creditScore;
        private decimal? _beginningBalance;
        private decimal? _endingBalance;
        private int? _remainingTerm;
        private int? _delinquencyDays;
        private decimal? _chargedOff;
        private decimal? _recovered;
        private short? _zeroBalanceCode;
        private DateOnly? _zeroBalanceDate;
        private int _zeroBalanceCodeCount;

        public void Apply(string name, string value)
        {
            switch (name)
            {
                case "assetNumber":
                    _assetNumber = value.Trim();
                    break;
                case "reportingPeriodBeginningDate":
                    _periodBegin = ParseDate(value);
                    break;
                case "reportingPeriodEndingDate":
                    _periodEnd = ParseDate(value);
                    break;
                case "originalLoanAmount":
                    _originalAmount = ParseDecimal(value);
                    break;
                case "originalLoanTerm":
                    _originalTerm = ParseInt(value);
                    break;
                case "originalInterestRatePercentage":
                    _originalRate = ParseDecimal(value);
                    break;
                case "reportingPeriodInterestRatePercentage":
                    _periodRate = ParseDecimal(value);
                    break;
                case "obligorCreditScore":
                    // STRING_20_TYPE in the schema: a non-numeric value is valid XML and
                    // becomes null rather than failing the file. GM Financial writes the
                    // literal text "None" 182 times in its 2026-08 exhibit.
                    //
                    // Zero is a sentinel for "no score available", not a score: Exeter
                    // reports 0 for 937 of 38,648 loans, all with obligorCreditScoreType
                    // "Consumer Credit Bureau", which is what a thin-file borrower looks
                    // like in a deep-subprime pool. Keeping it as a score would drag the
                    // weighted-average credit score down toward zero.
                    _creditScore = ParseInt(value) is > 0 and var score ? score : null;
                    break;
                case "reportingPeriodBeginningLoanBalanceAmount":
                    _beginningBalance = ParseDecimal(value);
                    break;
                case "reportingPeriodActualEndBalanceAmount":
                    _endingBalance = ParseDecimal(value);
                    break;
                case "remainingTermToMaturityNumber":
                    _remainingTerm = ParseInt(value);
                    break;
                case "currentDelinquencyStatus":
                    _delinquencyDays = ParseInt(value);
                    break;
                case "chargedoffPrincipalAmount":
                    _chargedOff = ParseDecimal(value);
                    break;
                case "recoveredAmount":
                    _recovered = ParseDecimal(value);
                    break;
                case "zeroBalanceCode":
                    _zeroBalanceCodeCount++;
                    _zeroBalanceCode ??= (short?)ParseInt(value);
                    break;
                case "zeroBalanceEffectiveDate":
                    _zeroBalanceDate = ParseMonthYear(value);
                    break;
                default:
                    break;
            }
        }

        public AutoLoanRecord? Build() =>
            string.IsNullOrWhiteSpace(_assetNumber)
                ? null  // assetNumber is the only minOccurs="1" element; without it there is no key.
                : new AutoLoanRecord
                {
                    TrustCik = TrustCik,
                    AssetNumber = _assetNumber,
                    ReportingPeriodEnd = _periodEnd ?? default,
                    ReportingPeriodBegin = _periodBegin,
                    OriginalLoanAmount = _originalAmount,
                    OriginalLoanTerm = _originalTerm,
                    OriginalInterestRatePercentage = _originalRate,
                    ReportingPeriodInterestRatePercentage = _periodRate,
                    ObligorCreditScore = _creditScore,
                    BeginningBalance = _beginningBalance,
                    EndingBalance = _endingBalance,
                    RemainingTermToMaturity = _remainingTerm,
                    CurrentDelinquencyDays = _delinquencyDays,
                    ChargedOffPrincipalAmount = _chargedOff,
                    RecoveredAmount = _recovered,
                    ZeroBalanceCode = _zeroBalanceCode,
                    ZeroBalanceEffectiveDate = _zeroBalanceDate,
                    ZeroBalanceCodeCount = _zeroBalanceCodeCount,
                };
    }

    /// <summary>
    /// <c>DATE_TYPE</c>: the schema's pattern is <c>\d{1,2}-\d{1,2}-\d{4}</c>, so
    /// <c>8-1-2026</c> is as valid as <c>08-01-2026</c>. A fixed <c>MM-dd-yyyy</c> parse
    /// would reject the short form.
    /// </summary>
    internal static DateOnly? ParseDate(string value) =>
        DateOnly.TryParseExact(
            value.Trim(), "M-d-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>
    /// <c>DATE_MM_YYYY_TYPE</c>: <c>MM/yyyy</c>, a different format from every other date in
    /// the same file. Normalised to the first of the month.
    /// </summary>
    internal static DateOnly? ParseMonthYear(string value) =>
        DateTime.TryParseExact(
            value.Trim(), "MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? DateOnly.FromDateTime(date)
            : null;

    internal static decimal? ParseDecimal(string value) =>
        decimal.TryParse(
            value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    internal static int? ParseInt(string value) =>
        int.TryParse(
            value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
}
