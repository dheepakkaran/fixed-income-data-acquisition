using System.Globalization;
using System.Text.Json;
using DataAcquisition.Core.Abstractions;
using DataAcquisition.Core.Model;
using Microsoft.Extensions.Logging;

namespace DataAcquisition.Providers.SecAbsEe.Http;

/// <summary>One ABS-EE filing found through the EDGAR submissions API.</summary>
/// <param name="AccessionNumber">Accession number with dashes, as EDGAR reports it.</param>
/// <param name="ReportingPeriod">The filing's period, from <c>reportDate</c>.</param>
/// <param name="FilingDate">When it was filed.</param>
public sealed record AbsEeFiling(string AccessionNumber, DateOnly ReportingPeriod, DateOnly FilingDate)
{
    /// <summary>Accession number without dashes, the form the Archives paths use.</summary>
    public string AccessionCompact => AccessionNumber.Replace("-", string.Empty, StringComparison.Ordinal);
}

/// <summary>
/// Reads EDGAR. Every request goes through the rate-limited, User-Agent-declaring
/// <see cref="HttpClient"/> configured in <c>ServiceCollectionExtensions</c>.
/// </summary>
public sealed class EdgarClient(
    HttpClient httpClient,
    IRawFileCache cache,
    ILogger<EdgarClient> logger)
{
    private static readonly JsonDocumentOptions JsonOptions = new() { AllowTrailingCommas = true };

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IRawFileCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    private readonly ILogger<EdgarClient> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Zero-pad a CIK to the ten digits the submissions API expects.</summary>
    public static string PadCik(string cik)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cik);
        return cik.Trim().TrimStart('0').PadLeft(10, '0');
    }

    /// <summary>CIK without leading zeros, the form the Archives paths use.</summary>
    public static string TrimCik(string cik)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cik);
        var trimmed = cik.Trim().TrimStart('0');
        return trimmed.Length == 0 ? "0" : trimmed;
    }

    /// <summary>ABS-EE filings for one trust, newest reporting period first.</summary>
    public async Task<IReadOnlyList<AbsEeFiling>> GetAbsEeFilingsAsync(
        string cik,
        CancellationToken cancellationToken)
    {
        var uri = new Uri($"https://data.sec.gov/submissions/CIK{PadCik(cik)}.json");

        using var stream = await _httpClient.GetStreamAsync(uri, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument
            .ParseAsync(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        var recent = document.RootElement.GetProperty("filings").GetProperty("recent");

        var forms = recent.GetProperty("form");
        var accessions = recent.GetProperty("accessionNumber");
        var reportDates = recent.GetProperty("reportDate");
        var filingDates = recent.GetProperty("filingDate");

        var filings = new List<AbsEeFiling>();
        for (var i = 0; i < forms.GetArrayLength(); i++)
        {
            if (!string.Equals(forms[i].GetString(), "ABS-EE", StringComparison.Ordinal))
            {
                continue;
            }

            var accession = accessions[i].GetString();
            if (string.IsNullOrWhiteSpace(accession)
                || !TryParseIsoDate(reportDates[i].GetString(), out var period))
            {
                continue;
            }

            TryParseIsoDate(filingDates[i].GetString(), out var filed);
            filings.Add(new AbsEeFiling(accession, period, filed));
        }

        _logger.LogDebug("CIK {Cik}: {Count} ABS-EE filing(s)", cik, filings.Count);
        return [.. filings.OrderByDescending(f => f.ReportingPeriod).ThenByDescending(f => f.AccessionNumber)];
    }

    /// <summary>
    /// Locate the EX-102 asset data file inside a filing. The document name differs per
    /// filing agent (<c>exh102...</c>, <c>..._exhibit102.xml</c>), so the index is read
    /// rather than the name guessed.
    /// </summary>
    public async Task<(string FileName, long SizeBytes)?> FindAssetDataFileAsync(
        string cik,
        AbsEeFiling filing,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filing);

        var uri = new Uri(
            $"https://www.sec.gov/Archives/edgar/data/{TrimCik(cik)}/{filing.AccessionCompact}/index.json");

        using var stream = await _httpClient.GetStreamAsync(uri, cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument
            .ParseAsync(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in document.RootElement.GetProperty("directory").GetProperty("item").EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name) || !IsAssetDataFile(name))
            {
                continue;
            }

            return (name, ReadSize(item));
        }

        _logger.LogWarning(
            "Filing {Accession} for CIK {Cik} has no EX-102 asset data file", filing.AccessionNumber, cik);
        return null;
    }

    /// <summary>
    /// The EX-102 asset data file, not the much smaller EX-103 asset representations file
    /// that sits beside it in every filing.
    /// </summary>
    internal static bool IsAssetDataFile(string name)
    {
        if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lower = name.ToLowerInvariant();
        return lower.Contains("exhibit102", StringComparison.Ordinal)
            || lower.Contains("exh102", StringComparison.Ordinal);
    }

    private static long ReadSize(JsonElement item) =>
        item.TryGetProperty("size", out var size)
            ? size.ValueKind switch
            {
                JsonValueKind.Number => size.GetInt64(),
                JsonValueKind.String when long.TryParse(
                    size.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                _ => 0L,
            }
            : 0L;

    /// <summary>
    /// Bring an exhibit to the local cache, downloading only when it is not already there.
    /// The cache key is the accession number plus the document name, so a given exhibit is
    /// fetched once however many times the run repeats.
    /// </summary>
    public async Task<FetchedSource> FetchExhibitAsync(
        DataSource source,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (_cache.TryGet(cacheKey, out var cachedPath))
        {
            var hash = await _cache.ComputeHashAsync(cachedPath, cancellationToken).ConfigureAwait(false);
            var size = new FileInfo(cachedPath).Length;
            _logger.LogInformation(
                "Cache hit for {Source} ({Size:N0} bytes); no request made", source, size);

            return new FetchedSource(source, cachedPath, hash, size, FromCache: true);
        }

        _logger.LogInformation("Downloading {Uri}", source.Uri);

        using var response = await _httpClient
            .GetAsync(source.Uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var content = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        var (path, sha256) = await _cache
            .WriteAsync(cacheKey, content, cancellationToken)
            .ConfigureAwait(false);

        var bytes = new FileInfo(path).Length;
        _logger.LogInformation("Cached {Source} as {Bytes:N0} bytes (sha256 {Hash})", source, bytes, sha256[..12]);

        return new FetchedSource(source, path, sha256, bytes, FromCache: false);
    }

    private static bool TryParseIsoDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
