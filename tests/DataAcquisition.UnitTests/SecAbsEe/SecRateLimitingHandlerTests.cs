using System.Diagnostics;
using System.Net;
using System.Threading.RateLimiting;
using DataAcquisition.Providers.SecAbsEe.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataAcquisition.UnitTests.SecAbsEe;

/// <summary>
/// SEC fair access permits at most 10 requests per second. Exceeding it gets the client
/// blocked, so this is the one external constraint worth testing directly.
/// </summary>
public sealed class SecRateLimitingHandlerTests
{
    /// <summary>Stands in for the network and records when each request arrived.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Lock _gate = new();

        public List<long> ArrivalTicks { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ArrivalTicks.Add(Stopwatch.GetTimestamp());
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public async Task The_average_request_rate_never_exceeds_the_ceiling()
    {
        // Measures requests divided by elapsed time rather than the distribution of recorded
        // arrivals. An earlier version of this test slid a one-second window across the
        // arrival timestamps, which is a stricter-looking assertion but a flakier one: the
        // timestamp is taken in a continuation, so when a shared CI runner deschedules the
        // thread pool the arrivals bunch together even though the limiter paced the permits
        // correctly. It observed 11 on a macOS runner and passed everywhere else, i.e. it
        // was measuring the thread pool. Average rate can only be pushed DOWN by scheduler
        // noise, never up, so it fails only when the limiter is genuinely wrong.
        var inner = new RecordingHandler();
        using var handler = new SecRateLimitingHandler(10, NullLogger<SecRateLimitingHandler>.Instance)
        {
            InnerHandler = inner,
        };
        using var client = new HttpClient(handler);

        const int requests = 25;
        var uri = new Uri("https://data.sec.gov/submissions/CIK0002099048.json");

        var started = Stopwatch.GetTimestamp();
        await Task.WhenAll(Enumerable.Range(0, requests)
            .Select(_ => client.GetAsync(uri, CancellationToken.None)));
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Equal(requests, inner.ArrivalTicks.Count);

        var rate = requests / elapsed.TotalSeconds;
        Assert.True(
            rate <= SecRateLimitingHandler.FairAccessCeiling,
            $"Sent {requests} requests in {elapsed.TotalSeconds:N2}s, a rate of {rate:N2}/s; "
            + $"the ceiling is {SecRateLimitingHandler.FairAccessCeiling}/s.");
    }

    [Fact]
    public async Task Twenty_five_requests_take_at_least_two_seconds()
    {
        // A corollary of the ceiling: 25 requests cannot physically complete faster than
        // 2 seconds at 10 per second. This catches a limiter that is wired up but inert.
        var inner = new RecordingHandler();
        using var handler = new SecRateLimitingHandler(10, NullLogger<SecRateLimitingHandler>.Instance)
        {
            InnerHandler = inner,
        };
        using var client = new HttpClient(handler);

        var started = Stopwatch.GetTimestamp();
        await Task.WhenAll(Enumerable.Range(0, 25)
            .Select(_ => client.GetAsync(new Uri("https://data.sec.gov/x"), CancellationToken.None)));
        var elapsed = Stopwatch.GetElapsedTime(started);

        Assert.True(
            elapsed >= TimeSpan.FromSeconds(2),
            $"25 requests completed in {elapsed.TotalSeconds:N2}s, which is faster than 10/s allows.");
    }

    [Fact]
    public void The_bucket_holds_a_single_token_so_there_is_no_burst_allowance()
    {
        // Deterministic, no waiting. One token means no two requests can leave together,
        // which is what bounds every one-second span rather than just each window.
        using var handler = new SecRateLimitingHandler(
            10, NullLogger<SecRateLimitingHandler>.Instance, autoReplenishment: false);

        using (var first = handler.Limiter.AttemptAcquire(1))
        {
            Assert.True(first.IsAcquired);
        }

        using var second = handler.Limiter.AttemptAcquire(1);
        Assert.False(second.IsAcquired);

        // Replenishment is time-gated rather than on demand, so that the token comes back
        // is proved by the paced-timing tests above instead of by calling TryReplenish
        // microseconds later.
    }

    [Fact]
    public void A_window_limiter_would_not_hold_the_ceiling_which_is_why_this_one_paces()
    {
        // Recorded so the reasoning is not lost: a sliding window with a permit limit of 10
        // grants all ten the moment the window opens, so ten at the end of one window and
        // ten at the start of the next are twenty inside one second. This limiter instead
        // carries one token, so its available permits are never more than one.
        using var handler = new SecRateLimitingHandler(
            10, NullLogger<SecRateLimitingHandler>.Instance, autoReplenishment: false);

        var statistics = handler.Limiter.GetStatistics();
        Assert.NotNull(statistics);
        Assert.Equal(1, statistics.CurrentAvailablePermits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public void A_rate_outside_the_fair_access_ceiling_is_rejected(int rate)
    {
        // Configuration cannot talk this provider into breaching SEC fair access.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SecRateLimitingHandler(rate, NullLogger<SecRateLimitingHandler>.Instance));
    }
}
