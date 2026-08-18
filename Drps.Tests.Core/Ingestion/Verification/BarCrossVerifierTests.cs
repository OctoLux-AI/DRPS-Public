using Drps.Ingestion.Verification;
using Drps.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Drps.Tests.Ingestion.Verification;

public class BarCrossVerifierTests
{
    private static readonly DateTime FetchedAt = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    private static OhlcvBar MakeBar(string ticker, DateOnly date, decimal close, string source) =>
        new(
            Ticker: ticker,
            BarDate: date,
            Open: close - 1m,
            High: close + 1m,
            Low: close - 2m,
            Close: close,
            Volume: 1_000_000L,
            Source: source,
            FetchedAt: FetchedAt,
            SampleCount: 1,
            VariancePct: null,
            Verified: false);

    [Fact]
    public void Merge_MatchingClosesWithinTolerance_VerifiedTrueAndSampleCountTwo()
    {
        var date = new DateOnly(2026, 7, 6);
        var alpaca = new[] { MakeBar("AAPL", date, 211.90m, "Alpaca") };
        var finnhub = new[] { MakeBar("AAPL", date, 212.00m, "Finnhub") }; // ~0.047% variance

        var logger = new CapturingLogger();
        var merged = BarCrossVerifier.Merge(alpaca, finnhub, logger);

        var bar = Assert.Single(merged);
        Assert.True(bar.Verified);
        Assert.Equal(2, bar.SampleCount);
        Assert.NotNull(bar.VariancePct);
        Assert.True(bar.VariancePct < 0.1m);
        Assert.Equal("Alpaca+Finnhub", bar.Source);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Merge_MismatchedCloses_VerifiedFalseAndVarianceLogged()
    {
        var date = new DateOnly(2026, 7, 6);
        var alpaca = new[] { MakeBar("AAPL", date, 211.90m, "Alpaca") };
        var finnhub = new[] { MakeBar("AAPL", date, 220.00m, "Finnhub") }; // ~3.82% variance

        var logger = new CapturingLogger();
        var merged = BarCrossVerifier.Merge(alpaca, finnhub, logger);

        var bar = Assert.Single(merged);
        Assert.False(bar.Verified);
        Assert.Equal(2, bar.SampleCount);
        Assert.NotNull(bar.VariancePct);
        Assert.True(bar.VariancePct > 0.1m);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("AAPL", warning.Message);
        Assert.Contains("211.90", warning.Message);
        Assert.Contains("220.00", warning.Message);
    }

    [Fact]
    public void Merge_BarOnlyInOneSource_StaysUnverifiedSingleSampleAndLogsNothing()
    {
        var date = new DateOnly(2026, 7, 8);
        var alpaca = new[] { MakeBar("AAPL", date, 211.90m, "Alpaca") };
        var finnhub = Array.Empty<OhlcvBar>();

        var logger = new CapturingLogger();
        var merged = BarCrossVerifier.Merge(alpaca, finnhub, logger);

        var bar = Assert.Single(merged);
        Assert.False(bar.Verified);
        Assert.Equal(1, bar.SampleCount);
        Assert.Null(bar.VariancePct);
        Assert.Equal("Alpaca", bar.Source);
        Assert.Empty(logger.Entries);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
