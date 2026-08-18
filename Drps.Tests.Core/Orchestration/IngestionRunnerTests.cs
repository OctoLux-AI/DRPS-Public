using Drps.Ingestion.Feeders;
using Drps.Ingestion.Orchestration;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Orchestration;

public class IngestionRunnerTests
{
    private static readonly DateOnly Day = new(2026, 7, 9);

    private static RawOhlcvBar MakeBar(SourceType source, string symbol) => new()
    {
        Source = source,
        Symbol = symbol,
        Timestamp = new DateTimeOffset(2026, 7, 9, 0, 0, 0, TimeSpan.Zero),
        Resolution = "1Day",
        Open = 100m,
        High = 101m,
        Low = 99m,
        Close = 100.5m,
        Volume = 1000,
        AdjustmentType = "raw",
        IngestedAt = DateTimeOffset.UtcNow,
        RequestId = Guid.NewGuid()
    };

    [Fact]
    public async Task RunAsync_OneFeederFailsOneSucceeds_PersistsSuccessfulFeedersBarsAndDoesNotThrow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var successFeeder = new FakeFeeder(SourceType.Alpaca, new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Alpaca, "AAPL"), MakeBar(SourceType.Alpaca, "AAPL") }
        });
        var failFeeder = new FakeFeeder(SourceType.AlphaVantage, new FeedFetchResult
        {
            Success = false,
            ErrorMessage = "boom"
        });

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { successFeeder, failFeeder }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        var exception = await Record.ExceptionAsync(() => runner.RunAsync("AAPL", Day, Day, CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal(2, await dbContext.RawOhlcvBars.CountAsync());
    }

    [Fact]
    public async Task RunAsync_BothFeedersReportResults_CreatesSourceStatusRowForEachRegardlessOfOutcome()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var successFeeder = new FakeFeeder(SourceType.Alpaca, new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Alpaca, "AAPL") }
        });
        var failFeeder = new FakeFeeder(SourceType.AlphaVantage, new FeedFetchResult
        {
            Success = false,
            ErrorMessage = "boom"
        });

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { successFeeder, failFeeder }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        await runner.RunAsync("AAPL", Day, Day, CancellationToken.None);

        var sources = await dbContext.SourceStatuses.Select(s => s.Source).ToListAsync();
        Assert.Contains(SourceType.Alpaca, sources);
        Assert.Contains(SourceType.AlphaVantage, sources);
    }

    [Fact]
    public async Task RunAsync_FeederThrowsDirectly_CatchesLogsContinuesAndDoesNotReportStatusForThatFeeder()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var throwingFeeder = new FakeFeeder(SourceType.Finnhub, () => throw new InvalidOperationException("boom"));
        var normalFeeder = new FakeFeeder(SourceType.Alpaca, new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Alpaca, "AAPL") }
        });

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { throwingFeeder, normalFeeder }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        var exception = await Record.ExceptionAsync(() => runner.RunAsync("AAPL", Day, Day, CancellationToken.None));

        Assert.Null(exception);

        // No FeedFetchResult was ever produced for the throwing feeder, so
        // SourceStatusTracker.RecordResultAsync was never called for it — only the
        // feeder that actually returned a result gets a row.
        var sources = await dbContext.SourceStatuses.Select(s => s.Source).ToListAsync();
        Assert.DoesNotContain(SourceType.Finnhub, sources);
        Assert.Contains(SourceType.Alpaca, sources);
    }

    [Fact]
    public async Task RunAsync_ThreeFeedersAllSucceed_PersistsAllBarsFromAllFeeders()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var feeder1 = new FakeFeeder(SourceType.Alpaca, new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Alpaca, "AAPL") }
        });
        var feeder2 = new FakeFeeder(SourceType.AlphaVantage, new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.AlphaVantage, "AAPL"), MakeBar(SourceType.AlphaVantage, "AAPL") }
        });
        var feeder3 = new FakeFeeder(SourceType.Finnhub, new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Finnhub, "AAPL"), MakeBar(SourceType.Finnhub, "AAPL"), MakeBar(SourceType.Finnhub, "AAPL") }
        });

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { feeder1, feeder2, feeder3 }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        await runner.RunAsync("AAPL", Day, Day, CancellationToken.None);

        Assert.Equal(1 + 2 + 3, await dbContext.RawOhlcvBars.CountAsync());
    }

    [Fact]
    public async Task RunAsync_FeederThrowsConfigurationMissingException_DoesNotCreateSourceStatusRow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var configErrorFeeder = new FakeFeeder(SourceType.Alpaca, () => throw new ConfigurationMissingException("Alpaca:KeyId"));

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { configErrorFeeder }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        var exception = await Record.ExceptionAsync(() => runner.RunAsync("AAPL", Day, Day, CancellationToken.None));

        Assert.Null(exception);

        // No FeedFetchResult was ever produced (the credential check throws before any HTTP
        // call), so RecordResultAsync was never called - no row at all, same as an
        // unexpected-throw feeder, and critically NOT the same as a recorded failure.
        var sources = await dbContext.SourceStatuses.Select(s => s.Source).ToListAsync();
        Assert.DoesNotContain(SourceType.Alpaca, sources);
    }

    [Fact]
    public async Task RunAsync_FeederThrowsConfigurationMissingException_ExistingSourceStatusConsecutiveFailuresUntouched()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        // Pre-seed a source already two strikes into the 3-strikes-dead threshold, mirroring
        // a source with real prior failures recorded before hitting a configuration bug.
        dbContext.SourceStatuses.Add(new SourceStatus
        {
            Source = SourceType.Alpaca,
            FieldOrBarType = SourceStatusTracker.DailyBarFieldOrBarType,
            TrustState = TrustState.Candidate,
            ConsecutiveFailures = 2,
            MatchedObservationCount = 0,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);
        var configErrorFeeder = new FakeFeeder(SourceType.Alpaca, () => throw new ConfigurationMissingException("Alpaca:SecretKey"));

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { configErrorFeeder }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        await runner.RunAsync("AAPL", Day, Day, CancellationToken.None);

        // A configuration error must never push an already-struggling source the rest of the
        // way to Dead - ConsecutiveFailures and TrustState must be exactly as seeded.
        var status = await dbContext.SourceStatuses.SingleAsync(s => s.Source == SourceType.Alpaca);
        Assert.Equal(2, status.ConsecutiveFailures);
        Assert.Equal(TrustState.Candidate, status.TrustState);
    }

    [Fact]
    public async Task RunAsync_FeederThrowsSymbolNotFoundException_DoesNotCreateSourceStatusRow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var notFoundFeeder = new FakeFeeder(SourceType.Tiingo, () => throw new SymbolNotFoundException("ZZZZINVALID"));

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { notFoundFeeder }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        var exception = await Record.ExceptionAsync(() => runner.RunAsync("ZZZZINVALID", Day, Day, CancellationToken.None));

        Assert.Null(exception);

        // A confirmed not-found is neither a success nor a real failure - no
        // RecordResultAsync call at all, same treatment as ConfigurationMissingException.
        var sources = await dbContext.SourceStatuses.Select(s => s.Source).ToListAsync();
        Assert.DoesNotContain(SourceType.Tiingo, sources);
    }

    [Fact]
    public async Task RunAsync_FeederThrowsSymbolNotFoundException_ExistingSourceStatusConsecutiveFailuresUntouched()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        // Pre-seed a source already two strikes into the 3-strikes-dead threshold, mirroring
        // a source with real prior failures recorded before hitting an invalid ticker.
        dbContext.SourceStatuses.Add(new SourceStatus
        {
            Source = SourceType.Tiingo,
            FieldOrBarType = SourceStatusTracker.DailyBarFieldOrBarType,
            TrustState = TrustState.Candidate,
            ConsecutiveFailures = 2,
            MatchedObservationCount = 0,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);
        var notFoundFeeder = new FakeFeeder(SourceType.Tiingo, () => throw new SymbolNotFoundException("ZZZZINVALID"));

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { notFoundFeeder }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        await runner.RunAsync("ZZZZINVALID", Day, Day, CancellationToken.None);

        // A bad ticker must never push an already-struggling source the rest of the way to
        // Dead - ConsecutiveFailures and TrustState must be exactly as seeded, unmoved in
        // either direction (not reset to 0 either, since this wasn't a real success).
        var status = await dbContext.SourceStatuses.SingleAsync(s => s.Source == SourceType.Tiingo);
        Assert.Equal(2, status.ConsecutiveFailures);
        Assert.Equal(TrustState.Candidate, status.TrustState);
    }

    [Fact]
    public async Task RunAsync_TiingoFeederReturnsNoDataForRange_ExistingSourceStatusMatchedObservationCountAndLastSuccessAtUntouched()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        // Confirms IngestionRunner's Success && NoDataForRange carve-out is generic, not
        // Alpaca-specific - the same code path now also protects Tiingo's 200+"[]" case
        // (a real, listed symbol whose range predates its IPO, falls entirely on
        // non-trading days, or is delisted with no data in that window).
        var seededLastSuccessAt = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.SourceStatuses.Add(new SourceStatus
        {
            Source = SourceType.Tiingo,
            FieldOrBarType = SourceStatusTracker.DailyBarFieldOrBarType,
            TrustState = TrustState.Candidate,
            ConsecutiveFailures = 0,
            MatchedObservationCount = 3,
            LastSuccessAt = seededLastSuccessAt,
            UpdatedAt = seededLastSuccessAt
        });
        await dbContext.SaveChangesAsync();

        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);
        var noDataFeeder = new FakeFeeder(SourceType.Tiingo, new FeedFetchResult
        {
            Success = true,
            Bars = Array.Empty<RawOhlcvBar>(),
            NoDataForRange = true
        });

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { noDataFeeder }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        await runner.RunAsync("ZZZZINVALID", Day, Day, CancellationToken.None);

        // A zero-bars response must not silently count toward the n=5 Trusted-promotion
        // threshold, and must not touch LastSuccessAt either - both must be exactly as
        // seeded, since this outcome confirms nothing about real data having landed.
        var status = await dbContext.SourceStatuses.SingleAsync(s => s.Source == SourceType.Tiingo);
        Assert.Equal(3, status.MatchedObservationCount);
        Assert.Equal(seededLastSuccessAt, status.LastSuccessAt);
    }

    [Fact]
    public async Task RunAsync_FeederReturnsNoDataForRange_DoesNotCreateSourceStatusRow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var noDataFeeder = new FakeFeeder(SourceType.Alpaca, new FeedFetchResult
        {
            Success = true,
            Bars = Array.Empty<RawOhlcvBar>(),
            NoDataForRange = true
        });

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { noDataFeeder }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        await runner.RunAsync("ZZZZINVALID", Day, Day, CancellationToken.None);

        // A well-formed but empty response is neither a real success nor a real failure -
        // no RecordResultAsync call at all, same treatment as ConfigurationMissingException
        // and SymbolNotFoundException.
        var sources = await dbContext.SourceStatuses.Select(s => s.Source).ToListAsync();
        Assert.DoesNotContain(SourceType.Alpaca, sources);
    }

    [Fact]
    public async Task RunAsync_FeederReturnsNoDataForRange_ExistingSourceStatusMatchedObservationCountAndLastSuccessAtUntouched()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var seededLastSuccessAt = DateTimeOffset.UtcNow.AddDays(-1);
        dbContext.SourceStatuses.Add(new SourceStatus
        {
            Source = SourceType.Alpaca,
            FieldOrBarType = SourceStatusTracker.DailyBarFieldOrBarType,
            TrustState = TrustState.Candidate,
            ConsecutiveFailures = 0,
            MatchedObservationCount = 3,
            LastSuccessAt = seededLastSuccessAt,
            UpdatedAt = seededLastSuccessAt
        });
        await dbContext.SaveChangesAsync();

        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);
        var noDataFeeder = new FakeFeeder(SourceType.Alpaca, new FeedFetchResult
        {
            Success = true,
            Bars = Array.Empty<RawOhlcvBar>(),
            NoDataForRange = true
        });

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { noDataFeeder }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        await runner.RunAsync("ZZZZINVALID", Day, Day, CancellationToken.None);

        // A zero-bars response must not silently count toward the n=5 Trusted-promotion
        // threshold, and must not touch LastSuccessAt either - both must be exactly as
        // seeded, since this outcome confirms nothing about real data having landed.
        var status = await dbContext.SourceStatuses.SingleAsync(s => s.Source == SourceType.Alpaca);
        Assert.Equal(3, status.MatchedObservationCount);
        Assert.Equal(seededLastSuccessAt, status.LastSuccessAt);
    }

    private static RawExDividendObservation MakeExDividendObservation(string symbol, decimal value) => new()
    {
        Source = SourceType.Tiingo,
        Symbol = symbol,
        ExDividendDate = Day,
        Value = value,
        SampleCount = 1,
        VariancePct = null,
        Verified = false,
        IngestedAt = DateTimeOffset.UtcNow,
        RequestId = Guid.NewGuid()
    };

    [Fact]
    public async Task RunAsync_ResultCarriesExDividendObservations_WritesThemIndependentlyOfBars()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var feeder = new FakeFeeder(SourceType.Tiingo, new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Tiingo, "AAPL") },
            ExDividendObservations = new[] { MakeExDividendObservation("AAPL", 0.24m) }
        });

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { feeder }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        await runner.RunAsync("AAPL", Day, Day, CancellationToken.None);

        Assert.Equal(1, await dbContext.RawOhlcvBars.CountAsync());
        var observation = Assert.Single(await dbContext.RawExDividendObservations.ToListAsync());
        Assert.Equal("AAPL", observation.Symbol);
        Assert.Equal(0.24m, observation.Value);
        Assert.Equal(SourceType.Tiingo, observation.Source);
    }

    [Fact]
    public async Task RunAsync_ResultCarriesNoExDividendObservations_WritesNoRowsAndDoesNotThrow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        // AlpacaFeeder never populates ExDividendObservations (FeedFetchResult's own default,
        // empty array) - confirms the Count > 0 guard means this is a true no-op, not an
        // empty AddRange/SaveChanges round-trip on every single-source feeder result.
        var feeder = new FakeFeeder(SourceType.Alpaca, new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Alpaca, "AAPL") }
        });

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { feeder }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        var exception = await Record.ExceptionAsync(() => runner.RunAsync("AAPL", Day, Day, CancellationToken.None));

        Assert.Null(exception);
        Assert.Empty(await dbContext.RawExDividendObservations.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_ExDividendObservationsWritten_DoesNotAffectSourceStatusForExDividendFieldType()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var feeder = new FakeFeeder(SourceType.Tiingo, new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Tiingo, "AAPL") },
            ExDividendObservations = new[] { MakeExDividendObservation("AAPL", 0.24m) }
        });

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { feeder }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        await runner.RunAsync("AAPL", Day, Day, CancellationToken.None);

        // Per CLAUDE.md's "Ex-Dividend Source: Tiingo Replaces Finnhub" decision: this write
        // deliberately does not route through SourceStatusTracker for the ExDividend
        // field/bar type — it rides the OHLCV fetch's own success/failure. Only the
        // DailyBar row should exist.
        var statuses = await dbContext.SourceStatuses.ToListAsync();
        var status = Assert.Single(statuses);
        Assert.Equal(SourceStatusTracker.DailyBarFieldOrBarType, status.FieldOrBarType);
        Assert.DoesNotContain(statuses, s => s.FieldOrBarType == SourceStatusTracker.ExDividendFieldOrBarType);
    }

    [Fact]
    public async Task RunAsync_FeederReturnsRealFailure_StillIncrementsConsecutiveFailuresAsBefore()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var failFeeder = new FakeFeeder(SourceType.Alpaca, new FeedFetchResult
        {
            Success = false,
            ErrorMessage = "401 Unauthorized"
        });

        var runner = new IngestionRunner(
            new IMarketDataFeeder[] { failFeeder }, dbContext, tracker, NullLogger<IngestionRunner>.Instance);

        await runner.RunAsync("AAPL", Day, Day, CancellationToken.None);

        // A real, attempted-and-rejected failure must still count as a strike exactly as it
        // did before this change - only the "never attempted" case is carved out.
        var status = await dbContext.SourceStatuses.SingleAsync(s => s.Source == SourceType.Alpaca);
        Assert.Equal(1, status.ConsecutiveFailures);
    }

    private sealed class FakeFeeder : IMarketDataFeeder
    {
        private readonly Func<Task<FeedFetchResult>> _handler;

        public FakeFeeder(SourceType source, FeedFetchResult result)
            : this(source, () => Task.FromResult(result))
        {
        }

        public FakeFeeder(SourceType source, Func<FeedFetchResult> handler)
            : this(source, () => Task.FromResult(handler()))
        {
        }

        private FakeFeeder(SourceType source, Func<Task<FeedFetchResult>> handler)
        {
            Source = source;
            _handler = handler;
        }

        public SourceType Source { get; }

        public Task<FeedFetchResult> FetchDailyBarsAsync(string symbol, DateOnly start, DateOnly end, CancellationToken cancellationToken)
            => _handler();
    }
}
