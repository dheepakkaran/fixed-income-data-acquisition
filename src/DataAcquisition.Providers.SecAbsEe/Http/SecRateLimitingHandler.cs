using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;

namespace DataAcquisition.Providers.SecAbsEe.Http;

/// <summary>
/// Holds every SEC request to the fair-access ceiling of 10 requests per second.
/// <para>
/// Implemented as an evenly paced token bucket of one token, replenished every
/// <c>1000 / rate</c> milliseconds, which spaces requests at least that far apart.
/// </para>
/// <para>
/// A window-based limiter was tried first and does not hold the ceiling. Both fixed and
/// sliding windows grant the whole permit limit the instant a window opens, so a client
/// can issue ten requests at the end of one window and ten more at the start of the next:
/// a test that slid a one-second window across recorded arrivals observed 19. Segmenting a
/// sliding window smooths when permits come back, not how many may be spent at once.
/// Pacing is what actually bounds every one-second span, and the cost is irrelevant here
/// because a run makes a few dozen requests.
/// </para>
/// </summary>
public sealed class SecRateLimitingHandler : DelegatingHandler
{
    /// <summary>SEC fair access allows no more than this many requests per second.</summary>
    public const int FairAccessCeiling = 10;

    /// <summary>
    /// Pacing is set this much slower than the configured rate. The replenishment timer
    /// fires on a best-effort cadence, so pacing at exactly the ceiling lets ordinary
    /// scheduler slack put two requests 98 ms apart and an observer counting any
    /// one-second span sees eleven. The ceiling is a limit to stay under, not a target to
    /// hit, and 5% of a few dozen requests costs nothing.
    /// </summary>
    private const double SafetyMargin = 1.05;

    private readonly TokenBucketRateLimiter _limiter;
    private readonly ILogger<SecRateLimitingHandler> _logger;

    public SecRateLimitingHandler(
        int maxRequestsPerSecond,
        ILogger<SecRateLimitingHandler> logger,
        bool autoReplenishment = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRequestsPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRequestsPerSecond, FairAccessCeiling);

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // One token at a time: no burst allowance, so no two requests can leave closer
        // together than the replenishment period.
        _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMilliseconds(1000.0 / maxRequestsPerSecond * SafetyMargin),
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = autoReplenishment,
        });
    }

    /// <summary>The limiter itself, so a test can drive replenishment instead of waiting.</summary>
    internal TokenBucketRateLimiter Limiter => _limiter;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var lease = await _limiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired)
        {
            throw new HttpRequestException(
                "The SEC rate limiter refused a permit; the request was not sent.");
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // SEC answers an over-rate or badly identified client with 403 rather than 429,
        // which is easy to mistake for an authorisation problem.
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            _logger.LogWarning(
                "SEC returned 403 for {Uri}. This usually means a missing or rejected User-Agent, or too many requests.",
                request.RequestUri);
        }

        return response;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _limiter.Dispose();
        }

        base.Dispose(disposing);
    }
}
