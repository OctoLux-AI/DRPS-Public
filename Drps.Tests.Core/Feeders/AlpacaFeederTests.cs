using System.Net;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Feeders;

public class AlpacaFeederTests
{
    private const string TwoBarFixture = """
    {
        "bars": [
            {"t": "2026-07-09T04:00:00Z", "o": 210.50, "h": 212.30, "l": 209.80, "c": 211.90, "v": 45123456, "n": 123456, "vw": 211.2},
            {"t": "2026-07-10T04:00:00Z", "o": 211.90, "h": 213.00, "l": 210.50, "c": 212.40, "v": 40234567, "n": 100000, "vw": 212.0}
        ],
        "symbol": "AAPL",
        "next_page_token": null
    }
    """;

    private static AlpacaFeeder CreateFeeder(FakeHttpMessageHandler handler, AlpacaOptions? options = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://data.alpaca.markets") };
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var resolvedOptions = Options.Create(options ?? new AlpacaOptions { KeyId = "test-key-id", SecretKey = "test-secret" });
        return new AlpacaFeeder(httpClientFactory, resolvedOptions, NullLogger<AlpacaFeeder>.Instance);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_NonMidnightAlpacaTimestamp_TruncatesToUtcMidnight()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TwoBarFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.True(result.Success);
        var first = result.Bars[0];
        Assert.Equal(new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero), first.Timestamp);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_Always_RequestsIexFeed()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TwoBarFixture) });
        var feeder = CreateFeeder(handler);

        await feeder.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("feed=iex", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_SuccessfulResponse_MapsAllFieldsCorrectly()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TwoBarFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Bars.Count);

        var first = result.Bars[0];
        Assert.Equal(SourceType.Alpaca, first.Source);
        Assert.Equal("AAPL", first.Symbol);
        Assert.Equal("1Day", first.Resolution);
        Assert.Equal(210.50m, first.Open);
        Assert.Equal(212.30m, first.High);
        Assert.Equal(209.80m, first.Low);
        Assert.Equal(211.90m, first.Close);
        Assert.Equal(45123456L, first.Volume);
        Assert.Equal("raw", first.AdjustmentType);

        var second = result.Bars[1];
        Assert.Equal(new DateTimeOffset(2026, 7, 10, 0, 0, 0, TimeSpan.Zero), second.Timestamp);
        Assert.Equal(212.40m, second.Close);
        Assert.Equal(40234567L, second.Volume);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_ZeroBarsInResponse_ReturnsSuccessButFlagsNoDataForRange()
    {
        // Confirmed empirically against the real API with an invalid ticker: Alpaca
        // returns 200 with an empty "bars" array, not a 404. This is the same shape it
        // would return for a legitimate but genuinely empty range (e.g. a market holiday
        // or a gap before a ticker's IPO date) - the feeder cannot and does not attempt
        // to tell those two cases apart from the response alone, so a single test fixture
        // covers both: whichever caused it, the outcome must be flagged the same way.
        const string zeroBarsFixture = """
        {
            "bars": [],
            "symbol": "ZZZZINVALID",
            "next_page_token": null
        }
        """;
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(zeroBarsFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "ZZZZINVALID", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Bars);
        Assert.True(result.NoDataForRange);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_DataBearingResponse_LeavesNoDataForRangeFalse()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TwoBarFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.NoDataForRange);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_NonRetryableClientError_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"message":"forbidden"}""")
            });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Bars);
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
            "AAPL", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.Equal(4, callCount);
        Assert.False(result.Success);
        Assert.Empty(result.Bars);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    // This test takes several real seconds to run, same as the 429 retry-exhaustion test
    // above - the retry policy's exponential backoff actually elapses.
    [Fact]
    public async Task FetchDailyBarsAsync_PersistentNetworkTimeout_RetriesThreeTimesThenReturnsFailure()
    {
        // Simulates HttpClient's own confirmed .NET 5+ behavior when its Timeout elapses:
        // a TaskCanceledException wrapping a TimeoutException, distinct from a caller-
        // triggered cancellation (which wouldn't carry that inner exception). Must get the
        // same 3-attempt exponential-backoff treatment as a 429/5xx, not a one-shot strike.
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
                new TimeoutException());
        });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

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
        // shutdown. This must propagate immediately, not be retried, so shutdown isn't
        // delayed by a pointless backoff.
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            throw new TaskCanceledException("The operation was canceled.");
        });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchDailyBarsAsync(
            "AAPL", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None);

        Assert.Equal(1, callCount);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_MissingKeyId_ThrowsConfigurationMissingExceptionWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP call should never have been attempted"));
        var feeder = CreateFeeder(handler, new AlpacaOptions { KeyId = "", SecretKey = "test-secret" });

        var exception = await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            feeder.FetchDailyBarsAsync("AAPL", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None));

        Assert.Equal("Alpaca:KeyId", exception.ConfigKey);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task FetchDailyBarsAsync_MissingSecretKey_ThrowsConfigurationMissingExceptionWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP call should never have been attempted"));
        var feeder = CreateFeeder(handler, new AlpacaOptions { KeyId = "test-key-id", SecretKey = "   " });

        var exception = await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            feeder.FetchDailyBarsAsync("AAPL", new DateOnly(2026, 7, 9), new DateOnly(2026, 7, 10), CancellationToken.None));

        Assert.Equal("Alpaca:SecretKey", exception.ConfigKey);
        Assert.Null(handler.LastRequest);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
