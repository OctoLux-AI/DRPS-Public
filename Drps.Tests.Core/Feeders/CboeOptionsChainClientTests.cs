using System.Net;
using Drps.Ingestion.Feeders;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Feeders;

public class CboeOptionsChainClientTests
{
    // Real confirmed Cboe delayed-quotes options shape (CBOE delayed-quotes options endpoint
    // audit, 2026-07-29): data.options[] is a flat array of per-contract rows, each with an
    // OCC-style `option` symbol and a per-contract `volume`. Fields this class doesn't read
    // (bid, ask, iv, open_interest, greeks, etc.) are omitted from these fixtures entirely.
    // Mixed calls/puts, distinct volumes each, so a bug that swapped call/put totals or summed
    // everything into one bucket would be caught.
    private const string MixedChainFixture = """
    {
        "timestamp": "2026-07-29 13:20:21",
        "data": {
            "symbol": "AAPL",
            "volume": 51859042,
            "options": [
                {"option": "AAPL260729C00205000", "volume": 10.0},
                {"option": "AAPL260729C00210000", "volume": 25.0},
                {"option": "AAPL260729P00205000", "volume": 5.0},
                {"option": "AAPL260729P00210000", "volume": 15.0}
            ]
        }
    }
    """;

    // Real confirmed zero-call-volume shape: a contract row with volume 0.0 is a legitimate,
    // frequently-observed value (per the live audit's own sample data), not a malformed field.
    private const string ZeroCallVolumeFixture = """
    {
        "data": {
            "options": [
                {"option": "AAPL260729C00205000", "volume": 0.0},
                {"option": "AAPL260729P00205000", "volume": 12.0}
            ]
        }
    }
    """;

    private const string EmptyOptionsArrayFixture = """{"data": {"options": []}}""";

    private static CboeOptionsChainClient CreateClient(
        FakeHttpMessageHandler handler, ILogger<CboeOptionsChainClient>? logger = null)
    {
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        return new CboeOptionsChainClient(httpClientFactory, logger ?? NullLogger<CboeOptionsChainClient>.Instance);
    }

    [Fact]
    public async Task GetPutCallRatioAsync_MixedCallAndPutVolumes_ReturnsCorrectRatio()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MixedChainFixture) });
        var client = CreateClient(handler);

        var result = await client.GetPutCallRatioAsync("AAPL", CancellationToken.None);

        // total call volume = 10 + 25 = 35, total put volume = 5 + 15 = 20 -> 20 / 35
        Assert.Equal(20.0m / 35.0m, result);
    }

    [Fact]
    public async Task GetPutCallRatioAsync_RequestsExpectedUrlWithUppercaseNoUnderscoreTicker()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MixedChainFixture) });
        var client = CreateClient(handler);

        await client.GetPutCallRatioAsync("aapl", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(
            "https://cdn.cboe.com/api/global/delayed_quotes/options/AAPL.json",
            handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetPutCallRatioAsync_ZeroTotalCallVolume_ReturnsNullNotDivideByZeroException()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ZeroCallVolumeFixture) });
        var client = CreateClient(handler);

        var result = await client.GetPutCallRatioAsync("AAPL", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPutCallRatioAsync_EmptyOptionsArray_ReturnsNull()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(EmptyOptionsArrayFixture) });
        var client = CreateClient(handler);

        var result = await client.GetPutCallRatioAsync("AAPL", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPutCallRatioAsync_MalformedJsonBody_ReturnsNullWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not valid json") });
        var client = CreateClient(handler);

        var result = await client.GetPutCallRatioAsync("AAPL", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPutCallRatioAsync_UnexpectedResponseShape_ReturnsNullWithoutThrowing()
    {
        // Valid JSON, but missing data.options entirely - a real contract-drift signal, same
        // "unexpected shape is a fetch-level failure" precedent as MarketauxSentimentClient.
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data": {"symbol": "AAPL"}}""") });
        var client = CreateClient(handler);

        var result = await client.GetPutCallRatioAsync("AAPL", CancellationToken.None);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task GetPutCallRatioAsync_NonSuccessStatus_ReturnsNullWithoutRetry(HttpStatusCode statusCode)
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(statusCode);
        });
        var client = CreateClient(handler);

        var result = await client.GetPutCallRatioAsync("AAPL", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetPutCallRatioAsync_HttpCallThrows_ReturnsNullWithoutRetry()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            throw new HttpRequestException("Connection refused");
        });
        var client = CreateClient(handler);

        var result = await client.GetPutCallRatioAsync("AAPL", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetPutCallRatioAsync_Timeout_ReturnsNullWithoutRetry()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout", new TimeoutException());
        });
        var client = CreateClient(handler);

        var result = await client.GetPutCallRatioAsync("AAPL", CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(1, callCount);
    }

    // --- Pure ComputePutCallRatio tests (no HTTP round-trip) ---

    [Fact]
    public void ComputePutCallRatio_TickerLettersOverlapCallPutChars_DoesNotMisreadTickerAsTypeChar()
    {
        // COST's own ticker contains 'C'; a naive Contains('C') check on the whole symbol
        // would misclassify this. The real type character is at the fixed strike-type
        // position (symbol[^9]) - here, 'P'.
        const string json = """{"data": {"options": [{"option": "COST260729P00600000", "volume": 7.0}]}}""";

        var result = CboeOptionsChainClient.ComputePutCallRatio(json, "COST");

        // total call volume = 0 -> null, per the same divide-by-zero guard, but critically
        // NOT because the 'C' in "COST" was mistaken for a call contract.
        Assert.Null(result);
    }

    [Fact]
    public void ComputePutCallRatio_UnparseableOptionSymbol_SkipsRowWithoutThrowing()
    {
        const string json = """
        {
            "data": {
                "options": [
                    {"option": "BAD", "volume": 99.0},
                    {"option": "AAPL260729C00205000", "volume": 10.0},
                    {"option": "AAPL260729P00205000", "volume": 5.0}
                ]
            }
        }
        """;

        var result = CboeOptionsChainClient.ComputePutCallRatio(json, "AAPL");

        Assert.Equal(5.0m / 10.0m, result);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
