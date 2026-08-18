using System.Net;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Feeders;

public class FinnhubSectorFeederTests
{
    // Well-documented Finnhub /stock/profile2 response shape: a bare top-level JSON object,
    // "finnhubIndustry" is the field this feeder reads.
    private const string ProfileFixture = """
    {
        "country": "US", "currency": "USD", "exchange": "NASDAQ NMS - GLOBAL MARKET",
        "finnhubIndustry": "Technology", "ipo": "1980-12-12", "marketCapitalization": 3000000,
        "name": "Apple Inc", "phone": "14089961010", "shareOutstanding": 15000,
        "ticker": "AAPL", "weburl": "https://www.apple.com/"
    }
    """;

    private static FinnhubSectorFeeder CreateFeeder(FakeHttpMessageHandler handler, FinnhubOptions? options = null)
    {
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var resolvedOptions = Options.Create(options ?? new FinnhubOptions { ApiKey = "test-key" });
        return new FinnhubSectorFeeder(httpClientFactory, resolvedOptions, NullLogger<FinnhubSectorFeeder>.Instance);
    }

    [Fact]
    public async Task FetchSectorAsync_SuccessfulResponse_MapsFieldsAndAuthenticatesViaHeader()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ProfileFixture) });
        var feeder = CreateFeeder(handler, new FinnhubOptions { ApiKey = "real-token" });

        var result = await feeder.FetchSectorAsync("AAPL", CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.NoDataForRange);
        var observation = Assert.Single(result.Observations);

        Assert.Equal("AAPL", observation.Ticker);
        Assert.Equal(SectorSourceType.Finnhub, observation.Source);
        Assert.Equal("Technology", observation.SectorValue);
        Assert.Null(observation.SicCode);
        Assert.True(observation.Verified);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("real-token", handler.LastRequest!.Headers.GetValues("X-Finnhub-Token").Single());
    }

    [Fact]
    public async Task FetchSectorAsync_EmptyObjectResponse_ReturnsSuccessWithNoObservationsAndNoDataForRangeTrue()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchSectorAsync("ZZZZ", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Observations);
        Assert.True(result.NoDataForRange);
    }

    [Fact]
    public async Task FetchSectorAsync_ObjectMissingFinnhubIndustry_ReturnsSuccessWithNoDataForRangeTrue()
    {
        const string noIndustryFixture = """{"country": "US", "currency": "USD", "ticker": "AAPL"}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(noIndustryFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchSectorAsync("AAPL", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Observations);
        Assert.True(result.NoDataForRange);
    }

    [Fact]
    public async Task FetchSectorAsync_ResponseIsNotAJsonObject_ReturnsFailureDistinctFromEmptyResult()
    {
        const string arrayBody = """[{"error": "Symbol not supported"}]""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(arrayBody) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchSectorAsync("ZZZZ", CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task FetchSectorAsync_UnparseableJsonBody_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not valid json") });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchSectorAsync("AAPL", CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task FetchSectorAsync_NonRetryableClientError_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"Invalid API key"}""")
            });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchSectorAsync("AAPL", CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task FetchSectorAsync_MissingApiKey_ThrowsConfigurationMissingExceptionWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP call should never have been attempted"));
        var feeder = CreateFeeder(handler, new FinnhubOptions { ApiKey = "" });

        var exception = await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            feeder.FetchSectorAsync("AAPL", CancellationToken.None));

        Assert.Equal("Finnhub:ApiKey", exception.ConfigKey);
        Assert.Null(handler.LastRequest);
    }

    // This test takes several real seconds: the retry policy's exponential backoff (2s, 4s,
    // 8s between attempts) actually elapses, same caveat as the existing Alpaca/Tiingo/
    // ex-dividend tests.
    [Fact]
    public async Task FetchSectorAsync_PersistentTransientFailure_RetriesThreeTimesThenReturnsFailure()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchSectorAsync("AAPL", CancellationToken.None);

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
