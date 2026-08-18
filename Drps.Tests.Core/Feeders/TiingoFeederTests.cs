using System.Net;
using Drps.Ingestion.Feeders;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Feeders;

public class TiingoFeederTests
{
    // Real Tiingo /tiingo/daily/{ticker}/prices response shape: a bare top-level JSON
    // array, raw and adjusted fields both present as real numbers (never JSON strings).
    private const string TwoBarFixture = """
    [
        {"date": "2026-07-09T00:00:00.000Z", "open": 210.50, "high": 212.30, "low": 209.80, "close": 211.90, "volume": 45123456,
         "adjOpen": 21.05, "adjHigh": 21.23, "adjLow": 20.98, "adjClose": 21.19, "adjVolume": 451234560, "divCash": 0.0, "splitFactor": 1.0},
        {"date": "2026-07-10T00:00:00.000Z", "open": 211.90, "high": 213.00, "low": 210.50, "close": 212.40, "volume": 40234567,
         "adjOpen": 21.19, "adjHigh": 21.30, "adjLow": 21.05, "adjClose": 21.24, "adjVolume": 402345670, "divCash": 0.0, "splitFactor": 1.0}
    ]
    """;

    private static TiingoFeeder CreateFeeder(FakeHttpMessageHandler handler, TiingoOptions? options = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.tiingo.com") };
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var resolvedOptions = Options.Create(options ?? new TiingoOptions { ApiKey = "test-key" });
        return new TiingoFeeder(httpClientFactory, resolvedOptions, NullLogger<TiingoFeeder>.Instance);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_SuccessfulResponse_MapsRawFieldsAsRealNumbersWithMidnightUtcTimestamp()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TwoBarFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "NVDA", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Bars.Count);

        var first = result.Bars[0];
        Assert.Equal(SourceType.Tiingo, first.Source);
        Assert.Equal("NVDA", first.Symbol);
        Assert.Equal("1Day", first.Resolution);
        Assert.Equal("raw", first.AdjustmentType);
        Assert.Equal(new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero), first.Timestamp);
        Assert.Equal(210.50m, first.Open);
        Assert.Equal(212.30m, first.High);
        Assert.Equal(209.80m, first.Low);
        Assert.Equal(211.90m, first.Close);
        Assert.Equal(45123456L, first.Volume);

        var second = result.Bars[1];
        Assert.Equal(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), second.Timestamp);
        Assert.Equal(212.40m, second.Close);
        Assert.Equal(40234567L, second.Volume);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_SuccessfulResponse_NeverReadsAdjustedFields()
    {
        // A split-affected fixture where raw and adjusted values genuinely diverge (~10x,
        // matching the empirically confirmed NVDA 2024-06-10 split behavior). If the feeder
        // ever accidentally read the adj* fields instead of the raw ones, this test would
        // catch it via the mapped Close value.
        const string splitAffectedFixture = """
        [
            {"date": "2024-06-07T00:00:00.000Z", "open": 1200.00, "high": 1210.00, "low": 1190.00, "close": 1200.00, "volume": 500000,
             "adjOpen": 120.00, "adjHigh": 121.00, "adjLow": 119.00, "adjClose": 120.00, "adjVolume": 5000000, "divCash": 0.0, "splitFactor": 1.0}
        ]
        """;
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(splitAffectedFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "NVDA", new DateOnly(2024, 6, 7), new DateOnly(2024, 6, 7), CancellationToken.None);

        Assert.True(result.Success);
        var bar = Assert.Single(result.Bars);
        Assert.Equal(1200.00m, bar.Open);
        Assert.Equal(1210.00m, bar.High);
        Assert.Equal(1190.00m, bar.Low);
        Assert.Equal(1200.00m, bar.Close);
        Assert.Equal(500000L, bar.Volume);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_NonRetryableClientError_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"detail":"Invalid Authorization Header"}""")
            });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "NVDA", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Bars);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_UnparseableJsonBody_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not valid json") });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "NVDA", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Bars);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_PlainTextUsageLimitBody_ReturnsFailureWithoutThrowing()
    {
        // Documented Tiingo failure mode: the free tier does not return a non-2xx status
        // when a usage limit is exceeded — it returns HTTP 200 with a plain-text body
        // instead of a JSON array, e.g. "You have run over your... limit for this month.
        // Please upgrade...". Must degrade to Success=false, never throw an unhandled
        // JSON parsing exception.
        const string usageLimitBody =
            "You have run over your allotted usage limit for this month. Please upgrade your Tiingo plan.";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(usageLimitBody) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "NVDA", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Bars);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_ResponseIsNotAJsonArray_ReturnsFailureDistinctFromEmptyResult()
    {
        const string errorObjectBody = """{"detail": "not found"}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(errorObjectBody) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "ZZZZ", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Bars);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_ResponseWithNonZeroDivCash_PopulatesExDividendObservations()
    {
        // AAPL 2023-08-11's divCash=0.24 matches a real, independently-verified ex-dividend
        // amount (CLAUDE.md's 2026-08-01 audit against stockanalysis.com) - confirms the
        // wiring end-to-end through the public FetchDailyBarsAsync entry point, not just the
        // mapper in isolation.
        const string dividendFixture = """
        [
            {"date": "2023-08-10T00:00:00.000Z", "open": 178.15, "high": 179.15, "low": 177.53, "close": 177.97, "volume": 54764400,
             "adjOpen": 175.9436, "adjHigh": 176.9314, "adjLow": 175.3315, "adjClose": 175.7614, "adjVolume": 54764400, "divCash": 0.0, "splitFactor": 1.0},
            {"date": "2023-08-11T00:00:00.000Z", "open": 177.32, "high": 178.62, "low": 176.55, "close": 177.79, "volume": 52036672,
             "adjOpen": 175.1211, "adjHigh": 176.4050, "adjLow": 174.3607, "adjClose": 175.5853, "adjVolume": 52036672, "divCash": 0.24, "splitFactor": 1.0}
        ]
        """;
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(dividendFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2023, 8, 10), new DateOnly(2023, 8, 11), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Bars.Count);

        var observation = Assert.Single(result.ExDividendObservations);
        Assert.Equal(SourceType.Tiingo, observation.Source);
        Assert.Equal("AAPL", observation.Symbol);
        Assert.Equal(new DateOnly(2023, 8, 11), observation.ExDividendDate);
        Assert.Equal(0.24m, observation.Value);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_NoNonZeroDivCashInResponse_ExDividendObservationsIsEmpty()
    {
        // TwoBarFixture's rows are both divCash=0.0 - confirms bars still map normally and
        // ExDividendObservations is empty, not just absent/null (FeedFetchResult's own
        // default), for a response that genuinely carries the field but no real event.
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TwoBarFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "NVDA", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Bars.Count);
        Assert.Empty(result.ExDividendObservations);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_MissingDivCashField_StillReturnsBarsSuccessfullyWithEmptyExDividendObservations()
    {
        // Simulates contract drift specifically on the divCash field (e.g. a future Tiingo
        // response shape change) - TiingoExDividendMapper.MapObservations will throw
        // (JsonElement.GetProperty throws when the property is absent), but TiingoFeeder's
        // own isolation try/catch around the mapper call must prevent that from failing the
        // whole fetch. OHLCV bars are what DMA/RSI/RVOL/ATR/Gate depend on downstream - a
        // dividend-extraction problem must never take real bar data down with it.
        const string noDivCashFixture = """
        [
            {"date": "2026-07-09T00:00:00.000Z", "open": 210.50, "high": 212.30, "low": 209.80, "close": 211.90, "volume": 45123456}
        ]
        """;
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(noDivCashFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "NVDA", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 9), CancellationToken.None);

        Assert.True(result.Success);
        var bar = Assert.Single(result.Bars);
        Assert.Equal(211.90m, bar.Close);
        Assert.Empty(result.ExDividendObservations);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_EmptyJsonArray_ReturnsSuccessWithNoBars()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "NVDA", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Bars);
        Assert.True(result.NoDataForRange);
    }

    // This test takes several real seconds to run: the retry policy's exponential backoff
    // (2s, 4s, 8s between attempts) actually elapses, since no fake time provider exists yet
    // to speed it up. Acceptable for now — flagged as a candidate for later if test runtime
    // becomes a problem.
    [Fact]
    public async Task FetchDailyBarsAsync_PersistentTransientFailure_RetriesThreeTimesThenReturnsFailure()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "NVDA", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.Equal(4, callCount);
        Assert.False(result.Success);
        Assert.Empty(result.Bars);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    // Same real-time backoff caveat as the 429 retry-exhaustion test above.
    [Fact]
    public async Task FetchDailyBarsAsync_PersistentNetworkTimeout_RetriesThreeTimesThenReturnsFailure()
    {
        // Simulates HttpClient's own confirmed .NET 5+ behavior when its Timeout elapses:
        // a TaskCanceledException wrapping a TimeoutException, distinct from a caller-
        // triggered cancellation (which wouldn't carry that inner exception). Must get the
        // same 3-attempt exponential-backoff treatment as a 429/5xx, not a one-shot strike.
        // Confirms the shared FeederRetryPolicy fix applies here too, not just to Alpaca.
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
                new TimeoutException());
        });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "NVDA", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.Equal(4, callCount);
        Assert.False(result.Success);
        Assert.Empty(result.Bars);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public async Task FetchDailyBarsAsync_CallerCancellationWithoutTimeout_DoesNotRetry()
    {
        // A TaskCanceledException NOT caused by HttpClient's own timeout (no inner
        // TimeoutException) represents the caller's CancellationToken firing - e.g. host
        // shutdown. This must propagate immediately, not be retried.
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            throw new TaskCanceledException("The operation was canceled.");
        });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "NVDA", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.Equal(1, callCount);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_NotFoundResponse_ThrowsSymbolNotFoundExceptionWithoutRetrying()
    {
        // Real Tiingo behavior for an unknown/delisted ticker: a genuine 404, not a 200
        // with an empty array. 404 isn't in TiingoFeeder's transient-retry set, so this
        // must resolve on the first call - retrying a confirmed not-found wastes calls
        // against the free tier's daily budget for no benefit.
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"detail":"Symbol not found"}""")
            };
        });
        var feeder = CreateFeeder(handler);

        var exception = await Assert.ThrowsAsync<SymbolNotFoundException>(() =>
            feeder.FetchDailyBarsAsync("ZZZZINVALID", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None));

        Assert.Equal("ZZZZINVALID", exception.Symbol);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_MissingApiKey_ThrowsConfigurationMissingExceptionWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP call should never have been attempted"));
        var feeder = CreateFeeder(handler, new TiingoOptions { ApiKey = "" });

        var exception = await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            feeder.FetchDailyBarsAsync("NVDA", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None));

        Assert.Equal("Tiingo:ApiKey", exception.ConfigKey);
        Assert.Null(handler.LastRequest);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
