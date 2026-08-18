using System.Net;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Feeders;

public class FinnhubExDividendFeederTests
{
    // Well-documented Finnhub /stock/dividend response shape: a bare top-level JSON array,
    // "date" is the ex-dividend date, "amount" is the raw declared per-share figure.
    private const string TwoObservationFixture = """
    [
        {"symbol": "AAPL", "date": "2026-05-09", "amount": 0.26, "adjustedAmount": 0.26,
         "payDate": "2026-05-15", "recordDate": "2026-05-12", "declarationDate": "2026-05-01", "currency": "USD"},
        {"symbol": "AAPL", "date": "2026-02-09", "amount": 0.25, "adjustedAmount": 0.25,
         "payDate": "2026-02-13", "recordDate": "2026-02-10", "declarationDate": "2026-01-29", "currency": "USD"}
    ]
    """;

    private static FinnhubExDividendFeeder CreateFeeder(FakeHttpMessageHandler handler, FinnhubOptions? options = null)
    {
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var resolvedOptions = Options.Create(options ?? new FinnhubOptions { ApiKey = "test-key" });
        return new FinnhubExDividendFeeder(httpClientFactory, resolvedOptions, NullLogger<FinnhubExDividendFeeder>.Instance);
    }

    [Fact]
    public async Task FetchExDividendsAsync_SuccessfulResponse_MapsFieldsAndAuthenticatesViaHeader()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TwoObservationFixture) });
        var feeder = CreateFeeder(handler, new FinnhubOptions { ApiKey = "real-token" });

        var result = await feeder.FetchExDividendsAsync(
            "AAPL", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.NoDataForRange);
        Assert.Equal(2, result.Observations.Count);

        var first = result.Observations[0];
        Assert.Equal(SourceType.Finnhub, first.Source);
        Assert.Equal("AAPL", first.Symbol);
        Assert.Equal(new DateOnly(2026, 5, 9), first.ExDividendDate);
        Assert.Equal(0.26m, first.Value);
        Assert.Equal(1, first.SampleCount);
        Assert.Null(first.VariancePct);
        Assert.False(first.Verified);

        var second = result.Observations[1];
        Assert.Equal(new DateOnly(2026, 2, 9), second.ExDividendDate);
        Assert.Equal(0.25m, second.Value);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("real-token", handler.LastRequest!.Headers.GetValues("X-Finnhub-Token").Single());
    }

    [Fact]
    public async Task FetchExDividendsAsync_SuccessfulResponse_NeverReadsAdjustedAmount()
    {
        // A fixture where amount and adjustedAmount genuinely diverge - if the feeder ever
        // accidentally read adjustedAmount instead of amount, this test would catch it.
        const string divergentFixture = """
        [
            {"symbol": "AAPL", "date": "2026-05-09", "amount": 0.26, "adjustedAmount": 0.13,
             "payDate": "2026-05-15", "recordDate": "2026-05-12", "declarationDate": "2026-05-01", "currency": "USD"}
        ]
        """;
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(divergentFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchExDividendsAsync(
            "AAPL", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1), CancellationToken.None);

        Assert.True(result.Success);
        var observation = Assert.Single(result.Observations);
        Assert.Equal(0.26m, observation.Value);
    }

    [Fact]
    public async Task FetchExDividendsAsync_EmptyJsonArray_ReturnsSuccessWithNoObservationsAndNoDataForRangeTrue()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchExDividendsAsync(
            "ZZZZ", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Observations);
        Assert.True(result.NoDataForRange);
    }

    [Fact]
    public async Task FetchExDividendsAsync_ResponseIsNotAJsonArray_ReturnsFailureDistinctFromEmptyResult()
    {
        const string errorObjectBody = """{"error": "Symbol not supported"}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(errorObjectBody) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchExDividendsAsync(
            "ZZZZ", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task FetchExDividendsAsync_UnparseableJsonBody_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not valid json") });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchExDividendsAsync(
            "AAPL", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task FetchExDividendsAsync_NonRetryableClientError_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"Invalid API key"}""")
            });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchExDividendsAsync(
            "AAPL", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task FetchExDividendsAsync_MissingApiKey_ThrowsConfigurationMissingExceptionWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP call should never have been attempted"));
        var feeder = CreateFeeder(handler, new FinnhubOptions { ApiKey = "" });

        var exception = await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            feeder.FetchExDividendsAsync("AAPL", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1), CancellationToken.None));

        Assert.Equal("Finnhub:ApiKey", exception.ConfigKey);
        Assert.Null(handler.LastRequest);
    }

    // This test takes several real seconds: the retry policy's exponential backoff (2s, 4s,
    // 8s between attempts) actually elapses, same caveat as the existing Alpaca/Tiingo tests.
    [Fact]
    public async Task FetchExDividendsAsync_PersistentTransientFailure_RetriesThreeTimesThenReturnsFailure()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchExDividendsAsync(
            "AAPL", new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 1), CancellationToken.None);

        Assert.Equal(4, callCount);
        Assert.False(result.Success);
        Assert.Empty(result.Observations);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
