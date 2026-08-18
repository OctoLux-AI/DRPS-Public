using System.Net;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Shared.Exceptions;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Feeders;

public class MarketauxSentimentClientTests
{
    // Real confirmed Marketaux /news/all shape (Marketaux diagnostic, 2026-07-24) - top-level
    // meta.found/meta.returned, data[].entities[].symbol/sentiment_score. Fields this class
    // doesn't read (title, url, highlights, etc.) are omitted from these fixtures entirely -
    // ParseResponse must not depend on anything beyond what it's documented to use.
    private const string RealPositiveScoreFixture = """
    {
        "meta": {"found": 87412, "returned": 1, "limit": 3, "page": 1},
        "data": [
            {"entities": [{"symbol": "NVDA", "sentiment_score": 0.223127}]}
        ]
    }
    """;

    private const string RealNegativeScoreFixture = """
    {
        "meta": {"found": 12, "returned": 1, "limit": 3, "page": 1},
        "data": [
            {"entities": [{"symbol": "NVDA", "sentiment_score": -0.4521}]}
        ]
    }
    """;

    private const string NullScoreFixture = """
    {
        "meta": {"found": 5, "returned": 1, "limit": 3, "page": 1},
        "data": [
            {"entities": [{"symbol": "NVDA", "sentiment_score": null}]}
        ]
    }
    """;

    // Confirmed real shape for a malformed/uncovered symbol (Marketaux diagnostic, 2026-07-24,
    // ZZZZ12345INVALID case): 200 status, empty data array.
    private const string EmptyDataFixture = """{"meta":{"found":0,"returned":0,"limit":3,"page":1},"data":[]}""";

    // Entities present, but none match the requested ticker (e.g. another article's
    // entities array mentions only unrelated tickers, a real observed shape per the
    // diagnostic's NVDA/TSLA/AAPL/GOOGL cross-mention finding).
    private const string NoMatchingEntityFixture = """
    {
        "meta": {"found": 40, "returned": 1, "limit": 3, "page": 1},
        "data": [
            {"entities": [{"symbol": "TSLA", "sentiment_score": 0.5}]}
        ]
    }
    """;

    private static MarketauxSentimentClient CreateClient(
        FakeHttpMessageHandler handler, MarketauxOptions? options = null, ILogger<MarketauxSentimentClient>? logger = null)
    {
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var resolvedOptions = Options.Create(options ?? new MarketauxOptions { ApiKey = "test-key" });
        return new MarketauxSentimentClient(httpClientFactory, resolvedOptions, logger ?? NullLogger<MarketauxSentimentClient>.Instance);
    }

    [Fact]
    public async Task GetSentimentAsync_RealPositiveScore_ReturnsRealValueOutcome()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RealPositiveScoreFixture) });
        var client = CreateClient(handler);

        var result = await client.GetSentimentAsync("NVDA", CancellationToken.None);

        Assert.Equal(MarketauxSentimentOutcome.RealValue, result.Outcome);
        Assert.Equal(0.223127m, result.SentimentScore);
        Assert.Equal(87412, result.ArticlesFound);
    }

    [Fact]
    public async Task GetSentimentAsync_RealNegativeScore_ReturnsRealValueOutcomeWithNegativeScore()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RealNegativeScoreFixture) });
        var client = CreateClient(handler);

        var result = await client.GetSentimentAsync("NVDA", CancellationToken.None);

        Assert.Equal(MarketauxSentimentOutcome.RealValue, result.Outcome);
        Assert.Equal(-0.4521m, result.SentimentScore);
    }

    [Fact]
    public async Task GetSentimentAsync_NullSentimentScoreOnMatchingEntity_ReturnsNullSentimentScoreOutcome()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(NullScoreFixture) });
        var client = CreateClient(handler);

        var result = await client.GetSentimentAsync("NVDA", CancellationToken.None);

        Assert.Equal(MarketauxSentimentOutcome.NullSentimentScore, result.Outcome);
        Assert.Null(result.SentimentScore);
    }

    [Fact]
    public async Task GetSentimentAsync_EmptyDataArray_ReturnsNoMatchingEntityOutcome()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(EmptyDataFixture) });
        var client = CreateClient(handler);

        var result = await client.GetSentimentAsync("ZZZZ12345INVALID", CancellationToken.None);

        Assert.Equal(MarketauxSentimentOutcome.NoMatchingEntity, result.Outcome);
        Assert.Null(result.SentimentScore);
        Assert.Equal(0, result.ArticlesFound);
    }

    [Fact]
    public async Task GetSentimentAsync_EntitiesPresentButNoneMatchTicker_ReturnsNoMatchingEntityOutcome()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(NoMatchingEntityFixture) });
        var client = CreateClient(handler);

        var result = await client.GetSentimentAsync("NVDA", CancellationToken.None);

        Assert.Equal(MarketauxSentimentOutcome.NoMatchingEntity, result.Outcome);
        Assert.Null(result.SentimentScore);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetSentimentAsync_NonSuccessStatus_ReturnsCallFailedOutcomeWithoutRetry(HttpStatusCode statusCode)
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("""{"error":{"code":"invalid_api_token","message":"An invalid API token was supplied."}}""")
            };
        });
        var client = CreateClient(handler);

        var result = await client.GetSentimentAsync("NVDA", CancellationToken.None);

        Assert.Equal(MarketauxSentimentOutcome.CallFailed, result.Outcome);
        Assert.Null(result.SentimentScore);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetSentimentAsync_Timeout_ReturnsCallFailedOutcomeWithoutRetry()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout", new TimeoutException());
        });
        var client = CreateClient(handler);

        var result = await client.GetSentimentAsync("NVDA", CancellationToken.None);

        Assert.Equal(MarketauxSentimentOutcome.CallFailed, result.Outcome);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetSentimentAsync_MalformedJsonBody_ReturnsCallFailedOutcome()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not valid json") });
        var client = CreateClient(handler);

        var result = await client.GetSentimentAsync("NVDA", CancellationToken.None);

        Assert.Equal(MarketauxSentimentOutcome.CallFailed, result.Outcome);
    }

    [Fact]
    public async Task GetSentimentAsync_MissingApiKey_ThrowsConfigurationMissingExceptionAndNeverAttemptsCall()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP call should never have been attempted"));
        var client = CreateClient(handler, new MarketauxOptions { ApiKey = "" });

        var exception = await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            client.GetSentimentAsync("NVDA", CancellationToken.None));

        Assert.Equal("Marketaux:ApiKey", exception.ConfigKey);
        Assert.Null(handler.LastRequest);
    }

    // CRITICAL instruction (Sentiment Adjuster Multiplier task): the API key must never reach
    // any log output, even on a failure path where the underlying exception message could
    // plausibly echo the request URL. Uses a real, distinctive key value so a leak would be
    // unambiguous if the redaction logic were broken.
    [Fact]
    public async Task GetSentimentAsync_FailureWithKeyEmbeddedInExceptionMessage_NeverLogsRawApiKey()
    {
        const string secretKey = "SUPER-SECRET-MARKETAUX-KEY-98765";
        var handler = new FakeHttpMessageHandler(request =>
            throw new HttpRequestException($"Connection failed for {request.RequestUri}"));
        var capturingLogger = new CapturingLogger<MarketauxSentimentClient>();
        var client = CreateClient(handler, new MarketauxOptions { ApiKey = secretKey }, capturingLogger);

        var result = await client.GetSentimentAsync("NVDA", CancellationToken.None);

        Assert.Equal(MarketauxSentimentOutcome.CallFailed, result.Outcome);
        Assert.All(capturingLogger.Messages, message => Assert.DoesNotContain(secretKey, message, StringComparison.Ordinal));
        Assert.Contains(capturingLogger.Messages, message => message.Contains("[REDACTED]", StringComparison.Ordinal));
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = new();
        private readonly object _lock = new();

        public IReadOnlyList<string> Messages
        {
            get { lock (_lock) { return _messages.ToList(); } }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_lock)
            {
                _messages.Add(message);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
