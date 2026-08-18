using System.Net;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Feeders;

public class SecEdgarSectorFeederTests
{
    private const string TickerMapFixture = """
    {
        "0": {"cik_str": 320193, "ticker": "AAPL", "title": "Apple Inc"}
    }
    """;

    // Trimmed to the fields this feeder actually reads - SEC's real submissions payload has
    // dozens of other fields (filings, addresses, former names, etc.) irrelevant here.
    private const string SubmissionsFixture = """
    {
        "cik": "320193", "sic": "3571", "sicDescription": "Electronic Computers", "name": "Apple Inc"
    }
    """;

    private static SecEdgarSectorFeeder CreateFeeder(
        FakeHttpMessageHandler handler, SecEdgarOptions? options = null, SecEdgarCikResolver? cikResolver = null)
    {
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var resolvedOptions = Options.Create(options ?? new SecEdgarOptions { UserAgent = "DRPS/1.0 (contact: test@example.com)" });
        var resolver = cikResolver ?? new SecEdgarCikResolver(httpClientFactory, resolvedOptions, NullLogger<SecEdgarCikResolver>.Instance);
        return new SecEdgarSectorFeeder(
            httpClientFactory, resolver, new SecEdgarRateLimiter(), resolvedOptions, NullLogger<SecEdgarSectorFeeder>.Instance);
    }

    // Routes by request URL so one fake handler can stand in for both the ticker-map
    // endpoint (hit once via the resolver) and the submissions endpoint (hit by this
    // feeder directly) - mirrors how the real IHttpClientFactory returns distinct
    // HttpClients that both ultimately reach sec.gov/data.sec.gov.
    private static FakeHttpMessageHandler CreateCombinedHandler(string submissionsResponseBody, HttpStatusCode submissionsStatus = HttpStatusCode.OK)
    {
        return new FakeHttpMessageHandler(request =>
        {
            if (request.RequestUri!.ToString().Contains("company_tickers.json"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TickerMapFixture) };

            return new HttpResponseMessage(submissionsStatus) { Content = new StringContent(submissionsResponseBody) };
        });
    }

    [Fact]
    public async Task FetchSectorAsync_ResolvableCik_MapsSicFieldsAndSetsVerifiedFalse()
    {
        var handler = CreateCombinedHandler(SubmissionsFixture);
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchSectorAsync("AAPL", CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.NoDataForRange);
        var observation = Assert.Single(result.Observations);

        Assert.Equal("AAPL", observation.Ticker);
        Assert.Equal(SectorSourceType.SecEdgar, observation.Source);
        Assert.Equal("Electronic Computers", observation.SectorValue);
        Assert.Equal("3571", observation.SicCode);
        Assert.False(observation.Verified);
    }

    [Fact]
    public async Task FetchSectorAsync_ResolvableCik_RequestsCorrectZeroPaddedSubmissionsUrl()
    {
        var handler = CreateCombinedHandler(SubmissionsFixture);
        var feeder = CreateFeeder(handler);

        await feeder.FetchSectorAsync("AAPL", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(
            "https://data.sec.gov/submissions/CIK0000320193.json",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("DRPS/1.0 (contact: test@example.com)", handler.LastRequest.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task FetchSectorAsync_UnknownTicker_NoCikResolved_ReturnsSuccessWithNoDataForRangeAndNoRequestSent()
    {
        var handler = CreateCombinedHandler(SubmissionsFixture);
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchSectorAsync("ZZZZINVALID", CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.NoDataForRange);
        Assert.Empty(result.Observations);

        // Only the ticker-map fetch should have happened - never a submissions request for
        // an unresolvable ticker.
        Assert.DoesNotContain("submissions", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task FetchSectorAsync_SubmissionsResponseMissingSic_ReturnsSuccessWithNoDataForRangeTrue()
    {
        const string noSicFixture = """{"cik": "320193", "name": "Apple Inc"}""";
        var handler = CreateCombinedHandler(noSicFixture);
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchSectorAsync("AAPL", CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.NoDataForRange);
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task FetchSectorAsync_SubmissionsResponseIsNotAJsonObject_ReturnsFailureDistinctFromEmptyResult()
    {
        var handler = CreateCombinedHandler("""[{"error": "not found"}]""");
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchSectorAsync("AAPL", CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task FetchSectorAsync_SubmissionsFetchNonRetryableError_ReturnsFailureWithoutThrowing()
    {
        var handler = CreateCombinedHandler("""{"error":"not found"}""", HttpStatusCode.NotFound);
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchSectorAsync("AAPL", CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Observations);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
