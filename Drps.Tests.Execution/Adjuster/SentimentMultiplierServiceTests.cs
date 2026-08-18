using System.Net;
using Drps.Adjuster.Configuration;
using Drps.Adjuster.Sentiment;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Adjuster;

public class SentimentMultiplierServiceTests
{
    // Today's stated starting values (Sentiment Adjuster Multiplier Decision, CLAUDE.md
    // 2026-07-24): multiplier = 1.0 + score x 1.0, clamped to [0.7, 1.3].
    private static readonly SentimentMultiplierOptions DefaultOptions = new()
    {
        ScalingFactor = 1.0m,
        MultiplierFloor = 0.7m,
        MultiplierCeiling = 1.3m
    };

    private static string ScoreFixture(decimal? score) => score is null
        ? """{"meta":{"found":1,"returned":1,"limit":3,"page":1},"data":[{"entities":[{"symbol":"NVDA","sentiment_score":null}]}]}"""
        : $$"""{"meta":{"found":1,"returned":1,"limit":3,"page":1},"data":[{"entities":[{"symbol":"NVDA","sentiment_score":{{score.Value}}}]}]}""";

    private const string EmptyDataFixture = """{"meta":{"found":0,"returned":0,"limit":3,"page":1},"data":[]}""";

    private static SentimentMultiplierService CreateService(
        FakeHttpMessageHandler handler,
        SentimentMultiplierOptions? options = null,
        ILogger<SentimentMultiplierService>? logger = null)
    {
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var marketauxOptions = Options.Create(new MarketauxOptions { ApiKey = "test-key" });
        var client = new MarketauxSentimentClient(httpClientFactory, marketauxOptions, NullLogger<MarketauxSentimentClient>.Instance);
        return new SentimentMultiplierService(
            client, Options.Create(options ?? DefaultOptions), logger ?? NullLogger<SentimentMultiplierService>.Instance);
    }

    [Fact]
    public async Task GetMultiplierAsync_NullSentimentScore_ReturnsNeutralMultiplierAndLogsDistinctCase()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ScoreFixture(null)) });
        var capturingLogger = new CapturingLogger<SentimentMultiplierService>();
        var service = CreateService(handler, logger: capturingLogger);

        var multiplier = await service.GetMultiplierAsync("NVDA", CancellationToken.None);

        Assert.Equal(1.0m, multiplier);
        Assert.Contains(capturingLogger.Messages, m => m.Contains("sentiment_score was null", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetMultiplierAsync_NoMatchingEntity_ReturnsNeutralMultiplierAndLogsDistinctCase()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(EmptyDataFixture) });
        var capturingLogger = new CapturingLogger<SentimentMultiplierService>();
        var service = CreateService(handler, logger: capturingLogger);

        var multiplier = await service.GetMultiplierAsync("ZZZZ12345INVALID", CancellationToken.None);

        Assert.Equal(1.0m, multiplier);
        Assert.Contains(capturingLogger.Messages, m => m.Contains("no matching entity", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetMultiplierAsync_CallFailure_ReturnsNeutralMultiplierLogsDistinctCaseAndDoesNotRetry(HttpStatusCode statusCode)
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(statusCode);
        });
        var capturingLogger = new CapturingLogger<SentimentMultiplierService>();
        var service = CreateService(handler, logger: capturingLogger);

        var multiplier = await service.GetMultiplierAsync("NVDA", CancellationToken.None);

        Assert.Equal(1.0m, multiplier);
        Assert.Equal(1, callCount);
        Assert.Contains(capturingLogger.Messages, m => m.Contains("call failed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetMultiplierAsync_Timeout_ReturnsNeutralMultiplierAndDoesNotRetry()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout", new TimeoutException());
        });
        var service = CreateService(handler);

        var multiplier = await service.GetMultiplierAsync("NVDA", CancellationToken.None);

        Assert.Equal(1.0m, multiplier);
        Assert.Equal(1, callCount);
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
