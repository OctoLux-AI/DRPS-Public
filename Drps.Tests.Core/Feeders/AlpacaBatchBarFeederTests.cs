using System.Net;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Feeders;

public class AlpacaBatchBarFeederTests
{
    // Real multi-symbol /v2/stocks/bars response shape: "bars" is an OBJECT keyed by symbol,
    // not the single-symbol endpoint's flat array - AAPL has one bar, MSFT has none (empty
    // array, the same "no data" shape as a symbol absent entirely), GOOG is absent from the
    // object altogether.
    private const string MultiSymbolFixture = """
    {
        "bars": {
            "AAPL": [
                {"t": "2026-07-21T04:00:00Z", "o": 210.50, "h": 212.30, "l": 209.80, "c": 211.90, "v": 45123456, "n": 123456, "vw": 211.2}
            ],
            "MSFT": []
        },
        "next_page_token": null
    }
    """;

    private static AlpacaBatchBarFeeder CreateFeeder(FakeHttpMessageHandler handler, AlpacaOptions? options = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://data.alpaca.markets") };
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var resolvedOptions = Options.Create(options ?? new AlpacaOptions { KeyId = "test-key-id", SecretKey = "test-secret" });
        return new AlpacaBatchBarFeeder(httpClientFactory, resolvedOptions, NullLogger<AlpacaBatchBarFeeder>.Instance);
    }

    [Fact]
    public async Task FetchBatchAsync_SuccessfulResponse_MapsFieldsWithDecimalPrecisionAndUtcMidnightTruncation()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MultiSymbolFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchBatchAsync(
            new[] { "AAPL", "MSFT", "GOOG" }, new DateOnly(2026, 7, 21), new DateOnly(2026, 7, 21), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.BarsBySymbol.ContainsKey("AAPL"));
        var bar = result.BarsBySymbol["AAPL"][0];

        Assert.Equal(SourceType.Alpaca, bar.Source);
        Assert.Equal("AAPL", bar.Symbol);
        Assert.Equal("1Day", bar.Resolution);
        Assert.Equal("raw", bar.AdjustmentType);
        Assert.Equal(210.50m, bar.Open);
        Assert.Equal(212.30m, bar.High);
        Assert.Equal(209.80m, bar.Low);
        Assert.Equal(211.90m, bar.Close);
        Assert.Equal(45123456L, bar.Volume);
        Assert.IsType<decimal>(bar.Close);

        // Alpaca's "t" (04:00:00Z) is midnight ET expressed in UTC, truncated to the UTC
        // date component - same rule AlpacaFeeder's single-symbol MapBars already applies.
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero), bar.Timestamp);
    }

    [Fact]
    public async Task FetchBatchAsync_SymbolWithEmptyArray_IsAbsentFromBarsBySymbol()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MultiSymbolFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchBatchAsync(
            new[] { "AAPL", "MSFT" }, new DateOnly(2026, 7, 21), new DateOnly(2026, 7, 21), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.BarsBySymbol.ContainsKey("MSFT"));
    }

    [Fact]
    public async Task FetchBatchAsync_SymbolAbsentFromResponse_IsAbsentFromBarsBySymbol()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MultiSymbolFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchBatchAsync(
            new[] { "AAPL", "GOOG" }, new DateOnly(2026, 7, 21), new DateOnly(2026, 7, 21), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.BarsBySymbol.ContainsKey("GOOG"));
        Assert.Single(result.BarsBySymbol);
    }

    [Fact]
    public async Task FetchBatchAsync_NextPageTokenPresent_FlagsResult()
    {
        const string paginatedFixture = """
        {
            "bars": { "AAPL": [{"t": "2026-07-21T04:00:00Z", "o": 1, "h": 1, "l": 1, "c": 1, "v": 1}] },
            "next_page_token": "abc123"
        }
        """;
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(paginatedFixture) });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchBatchAsync(
            new[] { "AAPL" }, new DateOnly(2026, 7, 21), new DateOnly(2026, 7, 21), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.NextPageTokenPresent);
    }

    [Fact]
    public async Task FetchBatchAsync_MissingKeyId_ThrowsConfigurationMissingException()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MultiSymbolFixture) });
        var feeder = CreateFeeder(handler, new AlpacaOptions { KeyId = "", SecretKey = "test-secret" });

        await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            feeder.FetchBatchAsync(new[] { "AAPL" }, new DateOnly(2026, 7, 21), new DateOnly(2026, 7, 21), CancellationToken.None));
    }

    [Fact]
    public async Task FetchBatchAsync_NonSuccessStatusCode_ReturnsFailureNotThrow()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("boom") });
        var feeder = CreateFeeder(handler);

        var result = await feeder.FetchBatchAsync(
            new[] { "AAPL" }, new DateOnly(2026, 7, 21), new DateOnly(2026, 7, 21), CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task FetchBatchAsync_RequestsCommaSeparatedSymbolsAndRawAdjustment()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(MultiSymbolFixture) });
        var feeder = CreateFeeder(handler);

        await feeder.FetchBatchAsync(
            new[] { "AAPL", "MSFT" }, new DateOnly(2026, 7, 21), new DateOnly(2026, 7, 21), CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        var query = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);
        Assert.Contains("symbols=AAPL,MSFT", query);
        Assert.Contains("adjustment=raw", query);
        Assert.Contains("feed=iex", query);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
