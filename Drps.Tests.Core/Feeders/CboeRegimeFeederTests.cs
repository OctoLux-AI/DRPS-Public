using System.Net;
using Drps.Ingestion.Feeders;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Feeders;

public class CboeRegimeFeederTests
{
    // Real Cboe VIX_History.csv shape, confirmed empirically 2026-07-26: header
    // "DATE,OPEN,HIGH,LOW,CLOSE", date as MM/dd/yyyy, six-decimal values.
    private const string ThreeRowFixture =
        "DATE,OPEN,HIGH,LOW,CLOSE\n" +
        "01/02/1990,17.240000,17.240000,17.240000,17.240000\n" +
        "07/23/2026,17.670000,20.310000,17.320000,18.700000\n" +
        "07/24/2026,18.960000,19.050000,17.410000,18.580000\n";

    private static CboeRegimeFeeder CreateFeeder(FakeHttpMessageHandler handler, string ticker = "VIX", string? url = null) =>
        new(new FakeHttpClientFactory(new HttpClient(handler)), ticker, url ?? CboeRegimeFeeder.VixUrl, NullLogger<CboeRegimeFeeder>.Instance);

    [Fact]
    public async Task FetchHistoryAsync_SuccessfulResponse_ParsesFullOhlcWithCboeDirectSource()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ThreeRowFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchHistoryAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, result.Observations.Count);

        var first = result.Observations[0];
        Assert.Equal("VIX", first.Ticker);
        Assert.Equal(RegimeSourceType.CboeDirect, first.Source);
        Assert.Equal(new DateOnly(1990, 1, 2), first.ObservationDate);
        Assert.Equal(17.24m, first.Open);
        Assert.Equal(17.24m, first.High);
        Assert.Equal(17.24m, first.Low);
        Assert.Equal(17.24m, first.Close);

        var last = result.Observations[2];
        Assert.Equal(new DateOnly(2026, 7, 24), last.ObservationDate);
        Assert.Equal(18.96m, last.Open);
        Assert.Equal(19.05m, last.High);
        Assert.Equal(17.41m, last.Low);
        Assert.Equal(18.58m, last.Close);
    }

    [Fact]
    public async Task FetchHistoryAsync_UsesTickerAndUrlSuppliedAtConstruction()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ThreeRowFixture) });
        var feeder = CreateFeeder(handler, "VXN", CboeRegimeFeeder.VxnUrl);

        var result = await feeder.FetchHistoryAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.All(result.Observations, o => Assert.Equal("VXN", o.Ticker));
        Assert.Equal(CboeRegimeFeeder.VxnUrl, handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task FetchHistoryAsync_UnexpectedHeader_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not a csv</html>") });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchHistoryAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task FetchHistoryAsync_MalformedRow_IsSkippedWithoutFailingWholeFetch()
    {
        const string fixtureWithBadRow =
            "DATE,OPEN,HIGH,LOW,CLOSE\n" +
            "01/02/1990,17.240000,17.240000,17.240000,17.240000\n" +
            "not-a-real-row\n" +
            "07/24/2026,18.960000,19.050000,17.410000,18.580000\n";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fixtureWithBadRow) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchHistoryAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Observations.Count);
    }

    [Fact]
    public async Task FetchHistoryAsync_NonRetryableClientError_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchHistoryAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    // This test takes several real seconds: the retry policy's exponential backoff (2s, 4s,
    // 8s) actually elapses, same documented caveat as TiingoFeederTests/AlpacaFeederTests.
    [Fact]
    public async Task FetchHistoryAsync_PersistentTransientFailure_RetriesThreeTimesThenReturnsFailure()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchHistoryAsync(CancellationToken.None);

        Assert.Equal(4, callCount);
        Assert.False(result.Success);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
