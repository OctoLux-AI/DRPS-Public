using Drps.Calculator.Persistence;
using Drps.Calculator.Verification;
using Drps.Gate.Positioning;
using Drps.Gate.Scoring;
using Drps.Ingestion.Persistence;
using Drps.Ingestion.Verification;
using Drps.Ledger;
using Drps.Shared.Models;
using Drps.Shared.Notifications;
using Drps.Shared.Positioning;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Gate;

public class GateScanServiceTests
{
    private static readonly DateOnly BarDate = new(2026, 7, 1);
    private static readonly DateTime AsOf = new(2026, 7, 1, 12, 0, 0);

    private static RawOhlcvBar MakeBar(string symbol, DateTimeOffset timestamp, decimal close) => new()
    {
        Source = SourceType.Alpaca,
        Symbol = symbol,
        Timestamp = timestamp,
        Resolution = "1Day",
        Open = close - 1m,
        High = close + 1m,
        Low = close - 2m,
        Close = close,
        Volume = 1_000_000,
        AdjustmentType = "raw",
        IngestedAt = DateTimeOffset.UtcNow,
        RequestId = Guid.NewGuid()
    };

    private static BarVerification MakeVerification(string symbol, DateTimeOffset timestamp) => new()
    {
        Symbol = symbol,
        Timestamp = timestamp,
        Resolution = "1Day",
        SourceCount = 2,
        MatchedSourceCount = 2,
        Verified = true,
        ToleranceApplied = 0.001m,
        ComputationVersion = 1,
        EvaluatedAt = DateTimeOffset.UtcNow
    };

    // `count` verified daily bars trailing backward from endDate (inclusive). Defaulting to
    // 60 covers every indicator's verification window (DMA's widest is 60, which subsumes
    // RVOL's 21 and RSI/ATR's 15 since all are trailing windows ending at the same anchor
    // date) - a smaller count is used deliberately in tests that need DMA's narrower windows
    // (5/15/30) to individually verify while its widest (60) does not, due to insufficient
    // total bar history.
    private static void SeedVerifiedBars(CalculatorDbContext dbContext, string symbol, DateOnly endDate, int count = 60)
    {
        for (var i = 0; i < count; i++)
        {
            var timestamp = new DateTimeOffset(endDate.AddDays(-i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar(symbol, timestamp, 100m + i));
            dbContext.BarVerifications.Add(MakeVerification(symbol, timestamp));
        }
    }

    private static void SeedDmaIndicators(
        CalculatorDbContext dbContext, string symbol, DateOnly barDate, bool aligned, bool hasTiingoCorrectedClose = false)
    {
        var values = aligned
            ? new Dictionary<int, decimal> { [5] = 50m, [15] = 40m, [30] = 30m, [60] = 20m }
            : new Dictionary<int, decimal> { [5] = 10m, [15] = 20m, [30] = 30m, [60] = 40m };

        foreach (var (window, value) in values)
        {
            dbContext.DmaIndicators.Add(new DmaIndicator
            {
                Symbol = symbol,
                BarDate = barDate,
                Window = window,
                Value = value,
                HasExDividendEvent = false,
                HasTiingoCorrectedClose = hasTiingoCorrectedClose,
                CalculationVersion = 1,
                ComputedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private static void SeedRsiIndicator(
        CalculatorDbContext dbContext, string symbol, DateOnly barDate, decimal value = 60m, bool hasTiingoCorrectedClose = false)
    {
        dbContext.RsiIndicators.Add(new RsiIndicator
        {
            Symbol = symbol,
            BarDate = barDate,
            Period = 14,
            Value = value,
            HasExDividendEvent = false,
            HasTiingoCorrectedClose = hasTiingoCorrectedClose,
            VerificationScopeLimited = true,
            CalculationVersion = 1,
            ComputedAt = DateTimeOffset.UtcNow
        });
    }

    // Sleeper Bucket tests need a real, multi-row RsiIndicator SERIES (not just the single
    // BarDate row SeedRsiIndicator above seeds) - RsiSlopeVerificationJoinService resolves
    // its "lookback positions back" endpoint by querying real RsiIndicator rows, so it needs
    // at least `lookback + 1` of them on or before `endDate` to succeed. One row per day,
    // ending at `endDate` (the most recent), matching SeedVerifiedBars' own day-indexing shape.
    private static void SeedRsiIndicatorRows(CalculatorDbContext dbContext, string symbol, DateOnly endDate, int count)
    {
        for (var i = 0; i < count; i++)
        {
            dbContext.RsiIndicators.Add(new RsiIndicator
            {
                Symbol = symbol,
                BarDate = endDate.AddDays(-i),
                Period = 14,
                Value = 60m,
                HasExDividendEvent = false,
                HasTiingoCorrectedClose = false,
                VerificationScopeLimited = true,
                CalculationVersion = 1,
                ComputedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private static void SeedRsiSlopeIndicator(
        CalculatorDbContext dbContext, string symbol, DateOnly barDate, int lookback, SlopeConfirmationDirection confirmedDirection)
    {
        dbContext.RsiSlopeIndicators.Add(new RsiSlopeIndicator
        {
            Symbol = symbol,
            BarDate = barDate,
            Lookback = lookback,
            Value = confirmedDirection == SlopeConfirmationDirection.ConfirmedPositive ? 10m : -10m,
            ConfirmedDirection = confirmedDirection,
            HasExDividendEvent = false,
            HasTiingoCorrectedClose = false,
            VerificationScopeLimited = true,
            CalculationVersion = 1,
            ComputedAt = DateTimeOffset.UtcNow
        });
    }

    private static void SeedRsiConcavityIndicator(
        CalculatorDbContext dbContext, string symbol, DateOnly barDate, int slopeLookback, SlopeConfirmationDirection confirmedDirection)
    {
        dbContext.RsiConcavityIndicators.Add(new RsiConcavityIndicator
        {
            Symbol = symbol,
            BarDate = barDate,
            SlopeLookback = slopeLookback,
            Value = confirmedDirection == SlopeConfirmationDirection.ConfirmedPositive ? 5m : -5m,
            ConfirmedDirection = confirmedDirection,
            HasExDividendEvent = false,
            HasTiingoCorrectedClose = false,
            VerificationScopeLimited = true,
            CalculationVersion = 1,
            ComputedAt = DateTimeOffset.UtcNow
        });
    }

    private static void SeedRvolIndicator(
        CalculatorDbContext dbContext, string symbol, DateOnly barDate, decimal value = 2.25m, bool hasTiingoCorrectedClose = false)
    {
        dbContext.RvolIndicators.Add(new RvolIndicator
        {
            Symbol = symbol,
            BarDate = barDate,
            BaselineWindow = 20,
            Value = value,
            HasExDividendEvent = false,
            HasTiingoCorrectedClose = hasTiingoCorrectedClose,
            CalculationVersion = 1,
            ComputedAt = DateTimeOffset.UtcNow
        });
    }

    private static void SeedAtrIndicator(
        CalculatorDbContext dbContext, string symbol, DateOnly barDate, decimal value = 2.5m, bool hasTiingoCorrectedClose = false)
    {
        dbContext.AtrIndicators.Add(new AtrIndicator
        {
            Symbol = symbol,
            BarDate = barDate,
            Period = 14,
            Value = value,
            HasExDividendEvent = false,
            HasTiingoCorrectedClose = hasTiingoCorrectedClose,
            VerificationScopeLimited = true,
            CalculationVersion = 1,
            ComputedAt = DateTimeOffset.UtcNow
        });
    }

    // Full, verified, aligned candidate - bars + all four indicator types at the same BarDate.
    // A well-formed candidate that clears every verification/alignment check; the resulting
    // composite score and bucket depend on GateQualityScorer/GateCompositeService's own
    // formula, which is redacted for public release - see README.md.
    private static void SeedAcceptedCandidate(CalculatorDbContext dbContext, string symbol, DateOnly barDate)
    {
        SeedVerifiedBars(dbContext, symbol, barDate);
        SeedDmaIndicators(dbContext, symbol, barDate, aligned: true);
        SeedRsiIndicator(dbContext, symbol, barDate);
        SeedRvolIndicator(dbContext, symbol, barDate);
        SeedAtrIndicator(dbContext, symbol, barDate);
    }

    // [REDACTED FOR PUBLIC RELEASE] Placeholder fixture values, not DRPS's real shipped
    // tuning - see README.md's "What's intentionally not public" section. Every RunScanAsync
    // test needs exactly one active row now that scoring is fail-closed without one. Returns
    // the persisted entity (its Id, assigned on save) so tests can assert
    // GateScore.GateParameterVersion against the real value rather than a guessed constant.
    private static async Task<GateParameters> SeedActiveGateParametersAsync(DrpsDbContext dbContext)
    {
        var parameters = new GateParameters
        {
            EffectiveFrom = BarDate.ToDateTime(TimeOnly.MinValue),
            IsActive = true,
            RsiLowerBound = 45m,
            RsiPeak = 55m,
            RsiUpperBound = 65m,
            RsiFloorQuality = 0.75m,
            RvolFloorMultiple = 1.2m,
            RvolCeilingMultiple = 2.8m,
            RvolFullWeight = 0.30m,
            RvolHalfWeight = 0.15m,
            RsiCompositeWeight = 0.70m,
            BuyThreshold = 0.85m,
            WatchThreshold = 0.75m,
            ExitThreshold = 0.70m,
            NoBuySessionCount = 2
        };

        dbContext.GateParameters.Add(parameters);
        await dbContext.SaveChangesAsync();

        return parameters;
    }

    // A fresh, verified Finnhub earnings observation with a next earnings date well outside
    // the 48-hour blackout window - EarningsLookupService.GetBlackoutStatusAsync's state (a),
    // "Clear." Every RunScanAsync test that asserts a specific non-capped Bucket (BUY in
    // particular) needs this seeded explicitly now that the earnings gate exists - an
    // unseeded ticker resolves to state (c), Unverified, per the gate's own fail-closed rule,
    // which would otherwise silently cap every such test's candidate at WATCH/NEUTRAL.
    private static async Task SeedClearEarningsAsync(DrpsDbContext dbContext, string ticker, DateTime asOf)
    {
        dbContext.RawEarningsObservations.Add(new RawEarningsObservation
        {
            Ticker = ticker,
            Source = SourceType.Finnhub,
            NextEarningsDate = DateOnly.FromDateTime(asOf).AddDays(30),
            FetchedAt = DateTime.UtcNow,
            FetchOutcome = EarningsFetchOutcome.UpcomingEarningsFound
        });
        await dbContext.SaveChangesAsync();
    }

    private static GateScanService CreateService(
        CalculatorDbContext calculatorDb,
        DrpsDbContext drpsDb,
        IPositionStateProvider? positionStateProvider = null,
        LedgerLifecycleStampService? lifecycleStampService = null,
        ILogger<GateScanService>? logger = null)
    {
        var rsiVerificationJoinService = new RsiVerificationJoinService(calculatorDb);
        return new GateScanService(
            calculatorDb,
            drpsDb,
            new DmaVerificationJoinService(calculatorDb),
            rsiVerificationJoinService,
            new RsiSlopeVerificationJoinService(calculatorDb, rsiVerificationJoinService),
            new RvolVerificationJoinService(calculatorDb),
            new AtrVerificationJoinService(calculatorDb),
            new SectorLookupService(drpsDb),
            new EarningsLookupService(drpsDb),
            positionStateProvider ?? new StubPositionStateProvider(),
            lifecycleStampService ?? new LedgerLifecycleStampService(
                drpsDb, new NoOpLifecycleNotificationService(), NullLogger<LedgerLifecycleStampService>.Instance),
            logger ?? NullLogger<GateScanService>.Instance);
    }

    // Tracks log levels and formatted messages - most call sites only need the level (a
    // Critical entry fired at all), but the GateParameters-validation-failure test also needs
    // to assert the specific violated rule is actually named in the message, not just that
    // some Critical log happened.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();

        public int CriticalCount => _entries.Count(e => e.Level == LogLevel.Critical);

        public IReadOnlyList<string> CriticalMessages =>
            _entries.Where(e => e.Level == LogLevel.Critical).Select(e => e.Message).ToList();

        public int WarningCount => _entries.Count(e => e.Level == LogLevel.Warning);

        public IReadOnlyList<string> WarningMessages =>
            _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    // Throws for one specific ticker, behaves like StubPositionStateProvider for every other -
    // used to deterministically force an unexpected mid-scan exception for exactly one
    // ticker, to test RunScanAsync's per-ticker isolation without modifying production code.
    private class ThrowingPositionStateProvider : IPositionStateProvider
    {
        private readonly string _throwForTicker;

        public ThrowingPositionStateProvider(string throwForTicker)
        {
            _throwForTicker = throwForTicker;
        }

        public bool IsCurrentlyHeld(string ticker)
        {
            if (ticker == _throwForTicker)
            {
                throw new InvalidOperationException($"Simulated failure for {ticker}");
            }

            return false;
        }

        public DateTime? GetNoBuyExpiration(string ticker) => null;
    }

    // A single RawOhlcvBar written directly into drpsDb.RawOhlcvBars - GateScanService's own
    // GetLatestCloseAsync (plateau reassessment's "current price") reads from DrpsDbContext,
    // not CalculatorDbContext, mirroring AdjusterScanService.GetLatestCloseAsync's identical
    // convention. Distinct from the calculator-side MakeBar/SeedVerifiedBars helpers above,
    // which seed CalculatorDbContext for indicator verification instead.
    private static RawOhlcvBar MakeDrpsPriceBar(string symbol, DateOnly date, decimal close) => new()
    {
        Source = SourceType.Alpaca,
        Symbol = symbol,
        Timestamp = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        Resolution = "1Day",
        Open = close - 1m,
        High = close + 1m,
        Low = close - 2m,
        Close = close,
        Volume = 1_000_000,
        AdjustmentType = "raw",
        IngestedAt = DateTimeOffset.UtcNow,
        RequestId = Guid.NewGuid()
    };

    // A real open Position for `ticker`, backed by its own unrelated GateScore/
    // AdjusterAllocation FK targets ("SEEDFK") - same shape as the composite-degradation test
    // above, factored out since every plateau test below needs one. baselinePrice/baselineDate
    // are passed through directly (including null, to exercise the no-baseline skip path).
    private static async Task<Position> SeedOpenPositionWithPlateauBaselineAsync(
        DrpsDbContext drpsDb, string ticker, decimal? baselinePrice, DateTime? baselineDate)
    {
        var seedGateScore = new GateScore
        {
            Ticker = "SEEDFK", Bucket = GateBucket.Buy, CompositeScore = 0.90m,
            ScanDate = BarDate.ToDateTime(TimeOnly.MinValue), CalculationVersion = 1, GateParameterVersion = 1
        };
        drpsDb.GateScores.Add(seedGateScore);
        await drpsDb.SaveChangesAsync();

        var seedAllocation = new AdjusterAllocation
        {
            GateScoreId = seedGateScore.Id, AllocationPercent = 0.03m, AllocationDollarAmount = 30000m,
            ShareCount = 300, ShareCapDeficient = false, AsOfTimestamp = BarDate.ToDateTime(TimeOnly.MinValue),
            AdjusterParameterVersion = 1
        };
        drpsDb.AdjusterAllocations.Add(seedAllocation);
        await drpsDb.SaveChangesAsync();

        var position = new Position
        {
            Ticker = ticker,
            GateScoreId = seedGateScore.Id,
            AdjusterAllocationId = seedAllocation.Id,
            EntryDate = BarDate.ToDateTime(TimeOnly.MinValue),
            EntryPrice = 100m,
            EntryQuantity = 50m,
            PlateauBaselinePrice = baselinePrice,
            PlateauBaselineDate = baselineDate
        };
        drpsDb.Positions.Add(position);
        await drpsDb.SaveChangesAsync();

        return position;
    }

    [Fact]
    public async Task RunScanAsync_PlateauNeitherAtrCrossNorDay5Elapsed_LeavesPositionAndBaselineUntouched()
    {
        using var calculatorDb = InMemoryCalculatorDbContextFactory.Create();
        using var drpsDb = InMemoryDbContextFactory.Create();

        const string ticker = "RRR";

        SeedAcceptedCandidate(calculatorDb, ticker, BarDate);
        await calculatorDb.SaveChangesAsync();
        await SeedActiveGateParametersAsync(drpsDb);

        // Baseline only 2 calendar days back (under day-5) and current price only 1 away from
        // baseline (well under 1.0x the default 2.5 ATR) - neither condition fires.
        var baselineDate = AsOf.AddDays(-2);
        var position = await SeedOpenPositionWithPlateauBaselineAsync(drpsDb, ticker, baselinePrice: 100m, baselineDate);

        drpsDb.RawOhlcvBars.Add(MakeDrpsPriceBar(ticker, BarDate, close: 101m));
        await drpsDb.SaveChangesAsync();

        var service = CreateService(calculatorDb, drpsDb);
        await service.RunScanAsync(AsOf, CancellationToken.None);

        var untouched = await drpsDb.Positions.SingleAsync(p => p.Id == position.Id);
        Assert.Null(untouched.PlateauDate);
        Assert.Null(untouched.ReactivatedDate);
        Assert.Null(untouched.DeactivatedDate);
        // Baseline is only ever reset when the trigger actually fires.
        Assert.Equal(100m, untouched.PlateauBaselinePrice);
        Assert.Equal(baselineDate, untouched.PlateauBaselineDate);
    }

    [Fact]
    public async Task RunScanAsync_OpenPositionWithNoPlateauBaseline_SkipsReassessmentWithoutThrowing()
    {
        using var calculatorDb = InMemoryCalculatorDbContextFactory.Create();
        using var drpsDb = InMemoryDbContextFactory.Create();

        const string ticker = "SSS";

        // Otherwise fully reassessable (would trigger via ATR-cross if it had a baseline) -
        // proves the skip is specifically about the missing baseline, not incomplete
        // indicator/price data.
        SeedAcceptedCandidate(calculatorDb, ticker, BarDate);
        await calculatorDb.SaveChangesAsync();
        await SeedActiveGateParametersAsync(drpsDb);

        // Simulates a Position row created before this schema existed - both baseline fields
        // null, distinct from "never triggered yet" (which would carry a real, non-null
        // baseline set at open time).
        var position = await SeedOpenPositionWithPlateauBaselineAsync(drpsDb, ticker, baselinePrice: null, baselineDate: null);

        drpsDb.RawOhlcvBars.Add(MakeDrpsPriceBar(ticker, BarDate, close: 150m));
        await drpsDb.SaveChangesAsync();

        var service = CreateService(calculatorDb, drpsDb);

        // Must not throw.
        await service.RunScanAsync(AsOf, CancellationToken.None);

        var untouched = await drpsDb.Positions.SingleAsync(p => p.Id == position.Id);
        Assert.Null(untouched.PlateauDate);
        Assert.Null(untouched.ReactivatedDate);
        Assert.Null(untouched.DeactivatedDate);
        Assert.Null(untouched.PlateauBaselinePrice);
        Assert.Null(untouched.PlateauBaselineDate);
    }

    // Earnings Blackout Gate Decision (CLAUDE.md 2026-07-19) - end-to-end wiring coverage
    // through the real GateScanService/EarningsLookupService pair, distinct from
    // GateQualityScorerTests' pure-logic coverage of the same gate.

    [Fact]
    public async Task RunScanAsync_RejectedCandidate_ProducesGateScoreRowWithNeutralBucketAndRejectionReason()
    {
        // Gate: Rejection Reasons Now Persisted (CLAUDE.md 2026-08-06) SUPERSEDES this test's
        // prior behavior - it used to assert no row was written on rejection; it now asserts
        // the opposite, deliberately, closing the "no persisted trail" gap the SBUX 2026-08-06
        // scan surfaced.
        using var calculatorDb = InMemoryCalculatorDbContextFactory.Create();
        using var drpsDb = InMemoryDbContextFactory.Create();

        // DMA misaligned (5<15<30<60) - rejected before GateCompositeService is ever reached,
        // no bars needed since alignment fails before verification matters. Not Sleeper-
        // eligible (no RsiSlope/RsiConcavity rows seeded), so this falls through to the
        // ordinary rejection-row path, not the Sleeper Bucket path.
        SeedDmaIndicators(calculatorDb, "BBB", BarDate, aligned: false);
        SeedRsiIndicator(calculatorDb, "BBB", BarDate);
        SeedRvolIndicator(calculatorDb, "BBB", BarDate);
        SeedAtrIndicator(calculatorDb, "BBB", BarDate);
        await calculatorDb.SaveChangesAsync();
        await SeedActiveGateParametersAsync(drpsDb);

        var service = CreateService(calculatorDb, drpsDb);
        await service.RunScanAsync(AsOf, CancellationToken.None);

        var score = Assert.Single(await drpsDb.GateScores.ToListAsync());
        Assert.Equal(GateBucket.Neutral, score.Bucket);
        Assert.Equal(nameof(GateRejectionReason.DmaNotAligned), score.RejectionReason);
        // No real composite scoring ever ran (Score() returns before reaching RsiQuality/
        // RvolQuality math on a DmaNotAligned rejection) - 0m is the honest "not applicable"
        // value, not a fabricated real score.
        Assert.Equal(0m, score.RsiQuality);
        Assert.Equal(0m, score.RvolQuality);
        Assert.Equal(0m, score.CompositeScore);
        // The real raw RSI/RVOL/ATR readings ARE carried through, since those were already
        // resolved before GateQualityScorer.Score ever ran.
        Assert.Equal(60m, score.RsiValue);
        Assert.Equal(2.25m, score.RvolValue);
        Assert.Equal(2.5m, score.AtrValue);
    }

    [Fact]
    public async Task RunScanAsync_GenuineBadBarInLastFiveDays_StillRejectsOutrightButProducesRejectionRow()
    {
        // Unchanged guardrail: a bad bar inside DMA-5's own lookback still hard-rejects (never
        // proceeds to composite scoring), same as before this decision - blast-radius scoping
        // narrows WHICH old bad bars can be tolerated, it does not weaken the check for a
        // genuinely recent one (matches the real SIRI case from the 2026-08-04 audit, whose
        // bad bar was rank 4). What DOES change, per Gate: Rejection Reasons Now Persisted
        // (CLAUDE.md 2026-08-06): the rejection is no longer silently discarded - a real
        // GateScore row is written naming DmaNotVerified as the cause.
        using var calculatorDb = InMemoryCalculatorDbContextFactory.Create();
        using var drpsDb = InMemoryDbContextFactory.Create();

        const string ticker = "RCT";
        SeedVerifiedBars(calculatorDb, ticker, BarDate, count: 60);

        // The 3rd-most-recent bar - inside every window's own lookback.
        var badBarDate = BarDate.AddDays(-2);
        var badBarTimestamp = new DateTimeOffset(badBarDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        await calculatorDb.SaveChangesAsync();
        var badBarVerification = await calculatorDb.BarVerifications.SingleAsync(v => v.Timestamp == badBarTimestamp);
        badBarVerification.Verified = false;

        SeedDmaIndicators(calculatorDb, ticker, BarDate, aligned: true);
        SeedRsiIndicator(calculatorDb, ticker, BarDate);
        SeedRvolIndicator(calculatorDb, ticker, BarDate);
        SeedAtrIndicator(calculatorDb, ticker, BarDate);
        await calculatorDb.SaveChangesAsync();
        await SeedActiveGateParametersAsync(drpsDb);

        var service = CreateService(calculatorDb, drpsDb);
        await service.RunScanAsync(AsOf, CancellationToken.None);

        var score = Assert.Single(await drpsDb.GateScores.Where(s => s.Ticker == ticker).ToListAsync());
        Assert.Equal(GateBucket.Neutral, score.Bucket);
        Assert.Equal(nameof(GateRejectionReason.DmaNotVerified), score.RejectionReason);
        Assert.Equal(0m, score.CompositeScore);
    }

    // Sleeper Bucket (CLAUDE.md 2026-08-04) - the three scenarios this decision's own
    // implementation task requires, end-to-end through the real RunScanAsync/GateScanService
    // pair (not just GateQualityScorer's pure passthrough, already covered separately in
    // GateQualityScorerTests).

    [Fact]
    public async Task RunScanAsync_DmaMisalignedWithBothMomentumConfirmedPositiveAndVerified_WritesSleeperRow()
    {
        using var calculatorDb = InMemoryCalculatorDbContextFactory.Create();
        using var drpsDb = InMemoryDbContextFactory.Create();

        const string ticker = "SLP";

        SeedVerifiedBars(calculatorDb, ticker, BarDate, count: 60);
        SeedDmaIndicators(calculatorDb, ticker, BarDate, aligned: false); // Tier 1 Stage 1 fails
        // Enough real RsiIndicator history (lookback=3 needs 4 rows minimum) for
        // RsiSlopeVerificationJoinService to resolve its "3 positions back" endpoint.
        SeedRsiIndicatorRows(calculatorDb, ticker, BarDate, count: 10);
        SeedRvolIndicator(calculatorDb, ticker, BarDate);
        SeedAtrIndicator(calculatorDb, ticker, BarDate);
        SeedRsiSlopeIndicator(calculatorDb, ticker, BarDate, lookback: 3, SlopeConfirmationDirection.ConfirmedPositive);
        SeedRsiConcavityIndicator(calculatorDb, ticker, BarDate, slopeLookback: 3, SlopeConfirmationDirection.ConfirmedPositive);
        await calculatorDb.SaveChangesAsync();
        await SeedActiveGateParametersAsync(drpsDb);

        var service = CreateService(calculatorDb, drpsDb);
        await service.RunScanAsync(AsOf, CancellationToken.None);

        var score = Assert.Single(await drpsDb.GateScores.Where(s => s.Ticker == ticker).ToListAsync());
        Assert.Equal(GateBucket.Sleeper, score.Bucket);
        Assert.False(score.IsDmaAligned);
        // No real composite scoring ever ran for this candidate (Score() returns before
        // reaching RsiQuality/RvolQuality math on a DmaNotAligned rejection) - 0m is the
        // honest "not applicable" value, not a fabricated real score.
        Assert.Equal(0m, score.RsiQuality);
        Assert.Equal(0m, score.RvolQuality);
        Assert.Equal(0m, score.CompositeScore);
        // The real raw RSI/RVOL/ATR readings ARE carried through, since those were already
        // resolved before GateQualityScorer.Score ever ran.
        Assert.Equal(60m, score.RsiValue);
        Assert.Equal(2.25m, score.RvolValue);
        Assert.Equal(2.5m, score.AtrValue);
    }

    [Fact]
    public async Task RunScanAsync_DmaMisalignedWithoutBothMomentumConfirmedPositive_ProducesOrdinaryRejectionRow()
    {
        // A ticker that's never been Sleeper-eligible and fails DMA alignment without
        // qualifying momentum falls through the two Sleeper-specific branches unchanged, same
        // as before this decision existed - but per Gate: Rejection Reasons Now Persisted
        // (CLAUDE.md 2026-08-06), it now lands on the ordinary rejection-row path instead of
        // being silently discarded.
        using var calculatorDb = InMemoryCalculatorDbContextFactory.Create();
        using var drpsDb = InMemoryDbContextFactory.Create();

        const string ticker = "NSL";

        SeedVerifiedBars(calculatorDb, ticker, BarDate, count: 60);
        SeedDmaIndicators(calculatorDb, ticker, BarDate, aligned: false);
        SeedRsiIndicatorRows(calculatorDb, ticker, BarDate, count: 10);
        SeedRvolIndicator(calculatorDb, ticker, BarDate);
        SeedAtrIndicator(calculatorDb, ticker, BarDate);
        // RsiSlope confirmed positive, but RsiConcavity is not - both must be
        // ConfirmedPositive, not just one.
        SeedRsiSlopeIndicator(calculatorDb, ticker, BarDate, lookback: 3, SlopeConfirmationDirection.ConfirmedPositive);
        SeedRsiConcavityIndicator(calculatorDb, ticker, BarDate, slopeLookback: 3, SlopeConfirmationDirection.Unconfirmed);
        await calculatorDb.SaveChangesAsync();
        await SeedActiveGateParametersAsync(drpsDb);

        var service = CreateService(calculatorDb, drpsDb);
        await service.RunScanAsync(AsOf, CancellationToken.None);

        var score = Assert.Single(await drpsDb.GateScores.Where(s => s.Ticker == ticker).ToListAsync());
        Assert.Equal(GateBucket.Neutral, score.Bucket);
        Assert.Equal(nameof(GateRejectionReason.DmaNotAligned), score.RejectionReason);
    }

    [Fact]
    public async Task RunScanAsync_PreviouslySleeperTickerNoLongerQualifies_WritesExplicitNeutralRowLeavingPriorSleeperRowUntouched()
    {
        using var calculatorDb = InMemoryCalculatorDbContextFactory.Create();
        using var drpsDb = InMemoryDbContextFactory.Create();

        const string ticker = "WKE";

        // A real prior Sleeper row, dated the day before today's scan - simulates a previous
        // night where this ticker genuinely qualified.
        var priorSleeperScore = new GateScore
        {
            Ticker = ticker,
            Bucket = GateBucket.Sleeper,
            CompositeScore = 0m,
            ScanDate = BarDate.AddDays(-1).ToDateTime(TimeOnly.MinValue),
            CalculationVersion = 1,
            GateParameterVersion = 1
        };
        drpsDb.GateScores.Add(priorSleeperScore);
        await drpsDb.SaveChangesAsync();

        SeedVerifiedBars(calculatorDb, ticker, BarDate, count: 60);
        SeedDmaIndicators(calculatorDb, ticker, BarDate, aligned: false);
        SeedRsiIndicatorRows(calculatorDb, ticker, BarDate, count: 10);
        SeedRvolIndicator(calculatorDb, ticker, BarDate);
        SeedAtrIndicator(calculatorDb, ticker, BarDate);
        // Momentum has since turned negative - no longer Sleeper-eligible today.
        SeedRsiSlopeIndicator(calculatorDb, ticker, BarDate, lookback: 3, SlopeConfirmationDirection.ConfirmedNegative);
        SeedRsiConcavityIndicator(calculatorDb, ticker, BarDate, slopeLookback: 3, SlopeConfirmationDirection.ConfirmedNegative);
        await calculatorDb.SaveChangesAsync();
        await SeedActiveGateParametersAsync(drpsDb);

        var service = CreateService(calculatorDb, drpsDb);
        await service.RunScanAsync(AsOf, CancellationToken.None);

        var allRowsForTicker = await drpsDb.GateScores
            .Where(s => s.Ticker == ticker)
            .OrderBy(s => s.ScanDate)
            .ToListAsync();

        Assert.Equal(2, allRowsForTicker.Count);

        // Prior Sleeper row: untouched, same Id, same Bucket - append-only, never mutated.
        Assert.Equal(priorSleeperScore.Id, allRowsForTicker[0].Id);
        Assert.Equal(GateBucket.Sleeper, allRowsForTicker[0].Bucket);
        Assert.Equal(priorSleeperScore.ScanDate, allRowsForTicker[0].ScanDate);

        // New row, this scan date: explicit Neutral, not silent absence - distinguishes
        // "momentum genuinely faded" from "a pipeline error stopped considering it."
        Assert.Equal(GateBucket.Neutral, allRowsForTicker[1].Bucket);
        Assert.Equal(AsOf, allRowsForTicker[1].ScanDate);
        Assert.False(allRowsForTicker[1].IsDmaAligned);
    }

    [Fact]
    public async Task RunScanAsync_NoActiveGateParametersRow_ProducesZeroGateScoreRowsAndLogsCriticalWithoutThrowing()
    {
        using var calculatorDb = InMemoryCalculatorDbContextFactory.Create();
        using var drpsDb = InMemoryDbContextFactory.Create();

        // A genuine, otherwise-fully-accepted candidate - proves the abort happens before any
        // candidate is ever looked at, not just that this particular candidate happens to fail.
        SeedAcceptedCandidate(calculatorDb, "III", BarDate);
        await calculatorDb.SaveChangesAsync();
        // Deliberately no SeedActiveGateParametersAsync call - drpsDb.GateParameters is empty.

        var capturingLogger = new CapturingLogger<GateScanService>();
        var service = CreateService(calculatorDb, drpsDb, logger: capturingLogger);

        // Must not throw - the fail-closed abort is a logged, handled outcome, not an
        // unhandled exception.
        await service.RunScanAsync(AsOf, CancellationToken.None);

        Assert.Empty(await drpsDb.GateScores.ToListAsync());
        Assert.True(capturingLogger.CriticalCount > 0);
    }

    [Fact]
    public async Task RunScanAsync_MultipleActiveGateParametersRows_ProducesZeroGateScoreRowsAndLogsCritical()
    {
        using var calculatorDb = InMemoryCalculatorDbContextFactory.Create();
        using var drpsDb = InMemoryDbContextFactory.Create();

        SeedAcceptedCandidate(calculatorDb, "JJJ", BarDate);
        await calculatorDb.SaveChangesAsync();
        // Two active rows should never happen in practice (GateParametersSeeder guards
        // against it) but RunScanAsync must check rather than assume exactly one exists.
        await SeedActiveGateParametersAsync(drpsDb);
        await SeedActiveGateParametersAsync(drpsDb);

        var capturingLogger = new CapturingLogger<GateScanService>();
        var service = CreateService(calculatorDb, drpsDb, logger: capturingLogger);

        await service.RunScanAsync(AsOf, CancellationToken.None);

        Assert.Empty(await drpsDb.GateScores.ToListAsync());
        Assert.True(capturingLogger.CriticalCount > 0);
    }

    [Fact]
    public async Task RunScanAsync_ActiveGateParametersRowFailsValidation_ProducesZeroGateScoreRowsAndNamesViolationInCriticalLog()
    {
        using var calculatorDb = InMemoryCalculatorDbContextFactory.Create();
        using var drpsDb = InMemoryDbContextFactory.Create();

        SeedAcceptedCandidate(calculatorDb, "KKK", BarDate);
        await calculatorDb.SaveChangesAsync();

        // A single active row exists (passes the prior task's existence check), but its own
        // values are nonsensical - RsiPeak == 0 is exactly the shape the migration's SQL-level
        // column defaults would produce for a row not inserted via GateParametersSeeder.
        var invalidParameters = await SeedActiveGateParametersAsync(drpsDb);
        invalidParameters.RsiLowerBound = -10m;
        invalidParameters.RsiPeak = 0m;
        await drpsDb.SaveChangesAsync();

        var capturingLogger = new CapturingLogger<GateScanService>();
        var service = CreateService(calculatorDb, drpsDb, logger: capturingLogger);

        await service.RunScanAsync(AsOf, CancellationToken.None);

        Assert.Empty(await drpsDb.GateScores.ToListAsync());
        Assert.True(capturingLogger.CriticalCount > 0);
        // Not just a generic rejection - the specific violated rule must be named.
        Assert.Contains(capturingLogger.CriticalMessages, m => m.Contains("RsiPeak must be > 0"));
    }
}
