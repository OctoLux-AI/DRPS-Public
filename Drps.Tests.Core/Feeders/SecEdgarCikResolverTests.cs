using System.Net;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Feeders;

public class SecEdgarCikResolverTests
{
    // SEC's real company_tickers.json shape: a JSON object keyed by an arbitrary numeric
    // index string, each value {"cik_str": int, "ticker": string, "title": string}.
    private const string TickerMapFixture = """
    {
        "0": {"cik_str": 320193, "ticker": "AAPL", "title": "Apple Inc"},
        "1": {"cik_str": 1045810, "ticker": "NVDA", "title": "NVIDIA CORP"}
    }
    """;

    private static SecEdgarCikResolver CreateResolver(FakeHttpMessageHandler handler, SecEdgarOptions? options = null)
    {
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var resolvedOptions = Options.Create(options ?? new SecEdgarOptions { UserAgent = "DRPS/1.0 (contact: test@example.com)" });
        return new SecEdgarCikResolver(httpClientFactory, resolvedOptions, NullLogger<SecEdgarCikResolver>.Instance);
    }

    [Fact]
    public void ParseTickerMap_FixturePayload_MapsTickersToZeroPaddedTenDigitCik()
    {
        var map = SecEdgarCikResolver.ParseTickerMap(TickerMapFixture);

        Assert.Equal("0000320193", map["AAPL"]);
        Assert.Equal("0001045810", map["NVDA"]);
    }

    [Fact]
    public async Task ResolveCikAsync_KnownTicker_ReturnsZeroPaddedCikAndSendsUserAgentHeader()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TickerMapFixture) });
        var resolver = CreateResolver(handler, new SecEdgarOptions { UserAgent = "DRPS/1.0 (contact: real@octolux.ai)" });

        var cik = await resolver.ResolveCikAsync("AAPL", CancellationToken.None);

        Assert.Equal("0000320193", cik);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("DRPS/1.0 (contact: real@octolux.ai)", handler.LastRequest!.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task ResolveCikAsync_UnknownTicker_ReturnsNullWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TickerMapFixture) });
        var resolver = CreateResolver(handler);

        var cik = await resolver.ResolveCikAsync("ZZZZINVALID", CancellationToken.None);

        Assert.Null(cik);
    }

    [Fact]
    public async Task ResolveCikAsync_CalledTwice_OnlyFetchesTickerMapOnce()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TickerMapFixture) };
        });
        var resolver = CreateResolver(handler);

        await resolver.ResolveCikAsync("AAPL", CancellationToken.None);
        await resolver.ResolveCikAsync("NVDA", CancellationToken.None);

        // 24-hour cache TTL means a second resolution within the same process shouldn't
        // trigger a second ~8MB fetch - the whole point of caching this file at all.
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ResolveCikAsync_FetchFails_ReturnsNullWithoutThrowing()
    {
        // Non-retryable status (404) so this test stays fast - the shared FeederRetryPolicy's
        // exhaustion timing is already exercised by other feeders' tests (e.g.
        // FinnhubSectorFeederTests).
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var resolver = CreateResolver(handler);

        var cik = await resolver.ResolveCikAsync("AAPL", CancellationToken.None);

        Assert.Null(cik);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
