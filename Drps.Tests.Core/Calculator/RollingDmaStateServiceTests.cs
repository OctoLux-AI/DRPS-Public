using Drps.Calculator.Dma;
using Drps.Calculator.Dma.RollingDma;
using Drps.Calculator.Persistence;
using Drps.Shared.Models;
using static Drps.Shared.Models.DmaAlignmentTerm;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Calculator;

public class RollingDmaStateServiceTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    private static RawOhlcvBar MakeBar(string symbol, DateOnly date, decimal close) => new()
    {
        Source = SourceType.Alpaca,
        Symbol = symbol,
        Timestamp = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        Resolution = "1Day",
        Open = close,
        High = close,
        Low = close,
        Close = close,
        Volume = 1_000_000,
        AdjustmentType = "raw",
        IngestedAt = DateTimeOffset.UtcNow,
        RequestId = Guid.NewGuid()
    };

    private static void SeedBars(CalculatorDbContext dbContext, string symbol, IReadOnlyList<decimal> closesInOrder)
    {
        for (var i = 0; i < closesInOrder.Count; i++)
        {
            dbContext.RawOhlcvBars.Add(MakeBar(symbol, FirstDay.AddDays(i), closesInOrder[i]));
        }
    }

    private static void SeedUniverse(CalculatorDbContext dbContext, DateOnly snapshotDate, params string[] tickers)
    {
        foreach (var ticker in tickers)
        {
            dbContext.UniverseSnapshots.Add(new UniverseSnapshot { SnapshotDate = snapshotDate, Ticker = ticker, Exchange = "NASDAQ" });
        }
    }

    [Fact]
    public async Task RunAsync_NoUniverseSnapshotForDate_CreatesNoStateAtAll()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);

        await service.RunAsync(FirstDay, CancellationToken.None);

        Assert.Empty(dbContext.RollingDmaStates);
        Assert.Empty(dbContext.DmaCrossingLogEntries);
    }

    [Fact]
    public async Task RunAsync_TickerWithNoBarsAtAll_SkipsItWithoutCreatingState()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var snapshotDate = FirstDay;
        SeedUniverse(dbContext, snapshotDate, "GHOST");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        Assert.Empty(dbContext.RollingDmaStates);
    }

    [Fact]
    public async Task RunAsync_FirstRun_BootstrapsAllFourTermsAndEmitsNoCrossingLogEntries()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        // 64 bars, ascending closes 1..64 - the 60-day window is fully populated (>=60 bars),
        // and every shorter-window trailing average exceeds every longer-window one for an
        // ascending series, so all four rows are aligned immediately at bootstrap: Term5/15/30
        // via their own pairwise comparisons, and FullStackAligned as the AND of those three
        // (Gate's own combined IsDmaAligned formula - RollingDmaAlignmentEvaluator's own doc
        // comment) - not an independent fourth term.
        var closes = Enumerable.Range(1, 64).Select(i => (decimal)i).ToList();
        var snapshotDate = FirstDay.AddDays(closes.Count - 1);
        SeedBars(dbContext, "AAPL", closes);
        SeedUniverse(dbContext, snapshotDate, "AAPL");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        var states = await dbContext.RollingDmaStates.Where(s => s.Ticker == "AAPL").ToListAsync();
        Assert.Equal(4, states.Count);

        var byTerm = states.ToDictionary(s => s.Term);
        Assert.Equal(62m, byTerm[5].Value); // avg(60..64)
        Assert.Equal(57m, byTerm[15].Value); // avg(50..64)
        Assert.Equal(49.5m, byTerm[30].Value); // avg(35..64)
        Assert.Equal(34.5m, byTerm[60].Value); // avg(5..64)

        Assert.True(byTerm[5].IsAligned);
        Assert.True(byTerm[15].IsAligned);
        Assert.True(byTerm[30].IsAligned);
        Assert.True(byTerm[60].IsAligned); // FullStackAligned - AND of the other three, all true here

        foreach (var state in states)
        {
            Assert.Equal(0, state.UpdatesSinceAnchor);
            Assert.False(state.InsufficientHistory);
        }

        // Bootstrap is initialization, not a genuine transition - no crossing entries logged.
        Assert.Empty(dbContext.DmaCrossingLogEntries);
    }

    [Fact]
    public async Task RunAsync_FewerBarsThanSmallestTerm_MarksInsufficientHistoryWithNullValue()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var closes = new List<decimal> { 10m, 20m, 30m }; // fewer than the smallest term (5)
        var snapshotDate = FirstDay.AddDays(closes.Count - 1);
        SeedBars(dbContext, "NEWCO", closes);
        SeedUniverse(dbContext, snapshotDate, "NEWCO");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        var states = await dbContext.RollingDmaStates.Where(s => s.Ticker == "NEWCO").ToListAsync();
        Assert.Equal(4, states.Count);
        foreach (var state in states)
        {
            Assert.Null(state.Value);
            Assert.True(state.InsufficientHistory);
            Assert.Equal(3, state.BarCount);
        }
    }

    [Fact]
    public async Task RunAsync_SecondRunOneNewBar_UpdatesIncrementallyNotFromScratch()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var closes = Enumerable.Range(1, 64).Select(i => (decimal)i).ToList();
        var day1SnapshotDate = FirstDay.AddDays(closes.Count - 1);
        SeedBars(dbContext, "AAPL", closes);
        SeedUniverse(dbContext, day1SnapshotDate, "AAPL");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(day1SnapshotDate, CancellationToken.None);

        // A 65th ascending day arrives.
        var day2SnapshotDate = day1SnapshotDate.AddDays(1);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", day2SnapshotDate, 65m));
        SeedUniverse(dbContext, day2SnapshotDate, "AAPL");
        await dbContext.SaveChangesAsync();

        await service.RunAsync(day2SnapshotDate, CancellationToken.None);

        var states = await dbContext.RollingDmaStates.Where(s => s.Ticker == "AAPL").ToListAsync();
        var byTerm = states.ToDictionary(s => s.Term);

        Assert.Equal(63m, byTerm[5].Value); // avg(61..65)
        Assert.Equal(58m, byTerm[15].Value); // avg(51..65)
        Assert.Equal(50.5m, byTerm[30].Value); // avg(36..65)
        Assert.Equal(35.5m, byTerm[60].Value); // avg(6..65)

        // Still ascending, still fully aligned on both sides of this update (true -> true for
        // all four rows) - no crossing entries logged since nothing actually flipped.
        Assert.True(byTerm[60].IsAligned); // FullStackAligned
        Assert.Empty(dbContext.DmaCrossingLogEntries);

        foreach (var state in states)
        {
            Assert.Equal(1, state.UpdatesSinceAnchor); // incremented, not reset - proves this was NOT a re-anchor/bootstrap
        }
    }

    [Fact]
    public async Task RunAsync_ThirdRunReversesTrend_LogsExitedForTermsThatFlipBack()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var closes = Enumerable.Range(1, 65).Select(i => (decimal)i).ToList(); // 1..65, days 1-2 combined
        var runOneDate = FirstDay.AddDays(63); // through close=64
        var runTwoDate = FirstDay.AddDays(64); // through close=65
        SeedBars(dbContext, "AAPL", closes);
        SeedUniverse(dbContext, runOneDate, "AAPL");
        SeedUniverse(dbContext, runTwoDate, "AAPL");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(runOneDate, CancellationToken.None); // bootstrap through close=64 - 5/15/30/60 all already aligned (ascending series)
        await service.RunAsync(runTwoDate, CancellationToken.None); // incremental, close=65 - still ascending, all four stay aligned, no crossing

        // A crash arrives on day 66 - close drops to 0.
        var runThreeDate = runTwoDate.AddDays(1);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", runThreeDate, 0m));
        SeedUniverse(dbContext, runThreeDate, "AAPL");
        await dbContext.SaveChangesAsync();

        await service.RunAsync(runThreeDate, CancellationToken.None);

        var states = await dbContext.RollingDmaStates.Where(s => s.Ticker == "AAPL").ToListAsync();
        var byTerm = states.ToDictionary(s => s.Term);

        Assert.Equal(50.8m, byTerm[5].Value); // avg(62,63,64,65,0)
        Assert.Equal(54.6m, byTerm[15].Value); // avg(52..65, 0)
        Assert.Equal(49.3m, byTerm[30].Value); // avg(37..65, 0)
        Assert.Equal(35.4m, byTerm[60].Value); // avg(7..65, 0)

        var thirdRunCrossings = await dbContext.DmaCrossingLogEntries
            .Where(c => c.TransitionDate == runThreeDate)
            .ToListAsync();

        // Term5 breaks its own pairwise comparison (50.8 not > 54.6); FullStackAligned - the
        // AND of Term5/15/30 (RollingDmaAlignmentEvaluator, matching Gate's own combined
        // IsDmaAligned formula), not an independent fourth term - exits as a direct consequence
        // of Term5 alone breaking, even though the 30-vs-60 pairwise relation itself
        // (49.3 > 35.4) still independently holds.
        Assert.Equal(2, thirdRunCrossings.Count);
        Assert.Contains(thirdRunCrossings, c => c.Term == Term5 && c.Direction == DmaCrossingDirection.Exited);
        Assert.Contains(thirdRunCrossings, c => c.Term == FullStackAligned && c.Direction == DmaCrossingDirection.Exited);

        // Term15 and Term30 stayed aligned throughout - no crossing logged for them on this run.
        Assert.DoesNotContain(thirdRunCrossings, c => c.Term == Term15);
        Assert.DoesNotContain(thirdRunCrossings, c => c.Term == Term30);
    }

    [Fact]
    public async Task RunAsync_SpikeAfterFlatBaseline_AllFourTermsEnterAlignmentTogetherAsAndOfTheOtherThree()
    {
        // A flat (all-equal) baseline means every pairwise comparison (5>15, 15>30, 30>60)
        // starts false - nothing is "greater than," everything is tied. Isolates
        // FullStackAligned's AND-of-the-other-three behavior cleanly: since Term5/15/30 all
        // flip true simultaneously off the same single spike bar, FullStackAligned must flip
        // true on that exact same date too, never independently or on a different day (it is
        // not a fourth independent term - RollingDmaAlignmentEvaluator's own doc comment).
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var flatCloses = Enumerable.Repeat(100m, 64).ToList();
        var bootstrapDate = FirstDay.AddDays(63);
        SeedBars(dbContext, "SPIKECO", flatCloses);
        SeedUniverse(dbContext, bootstrapDate, "SPIKECO");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(bootstrapDate, CancellationToken.None);

        var bootstrapStates = await dbContext.RollingDmaStates.Where(s => s.Ticker == "SPIKECO").ToListAsync();
        Assert.All(bootstrapStates, s => Assert.False(s.IsAligned)); // flat - nothing strictly greater than anything
        Assert.Empty(dbContext.DmaCrossingLogEntries);

        // One spike bar (100 -> 160) on top of the flat baseline.
        var spikeDate = bootstrapDate.AddDays(1);
        dbContext.RawOhlcvBars.Add(MakeBar("SPIKECO", spikeDate, 160m));
        SeedUniverse(dbContext, spikeDate, "SPIKECO");
        await dbContext.SaveChangesAsync();

        await service.RunAsync(spikeDate, CancellationToken.None);

        var states = await dbContext.RollingDmaStates.Where(s => s.Ticker == "SPIKECO").ToListAsync();
        var byTerm = states.ToDictionary(s => s.Term);

        Assert.Equal(112m, byTerm[5].Value); // avg(100,100,100,100,160)
        Assert.Equal(104m, byTerm[15].Value); // avg(14x100, 160)
        Assert.Equal(102m, byTerm[30].Value); // avg(29x100, 160)
        Assert.Equal(101m, byTerm[60].Value); // avg(59x100, 160)

        Assert.All(states, s => Assert.True(s.IsAligned));

        var crossings = await dbContext.DmaCrossingLogEntries.Where(c => c.Ticker == "SPIKECO").ToListAsync();
        Assert.Equal(4, crossings.Count);
        Assert.All(crossings, c => Assert.Equal(DmaCrossingDirection.Entered, c.Direction));
        Assert.All(crossings, c => Assert.Equal(spikeDate, c.TransitionDate));
        Assert.Equal(
            new[] { Term5, Term15, Term30, FullStackAligned }.OrderBy(t => t),
            crossings.Select(c => c.Term).OrderBy(t => t));
    }

    [Fact]
    public async Task RunAsync_TiingoCorrectedBarInWindow_Term5MatchesDmaComputationServiceForSameDate()
    {
        // Reproduces the audit finding (CLAUDE.md, "Fix RollingDmaStateService to use
        // Tiingo-corrected Close prices" task, 2026-07-31): a bar whose Alpaca Close was
        // resolved to Tiingo's corrected Close via the narrow OHL-agreed/Close-disagreed
        // exception (BarReconciliationService) must feed RollingDmaStateService's SMA the same
        // way it already feeds DmaComputationService's - otherwise RollingDmaStates silently
        // diverges from Gate's authoritative DmaIndicators for the same window, exactly as
        // hand-verified on HON/MSFT in that audit. The dollar figures here are a synthetic
        // reproduction of that divergence shape (same mechanism, not the literal audited
        // values) - the assertion that matters is structural: both services must land on the
        // identical Value once both correctly resolve the same corrected Close.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        const string ticker = "HON";

        var precedingCloses = new[] { 200m, 202m, 204m, 206m };
        for (var i = 0; i < precedingCloses.Length; i++)
        {
            dbContext.RawOhlcvBars.Add(MakeBar(ticker, FirstDay.AddDays(i), precedingCloses[i]));
        }

        var correctedDate = FirstDay.AddDays(4);
        var correctedTimestamp = new DateTimeOffset(correctedDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        const decimal alpacaClose = 300m; // raw Alpaca print, confirmed incorrect
        const decimal tiingoClose = 210m; // corrected Close, per BarVerification.PrimarySourceValue

        dbContext.RawOhlcvBars.Add(MakeBar(ticker, correctedDate, alpacaClose));
        dbContext.BarVerifications.Add(new BarVerification
        {
            Symbol = ticker,
            Timestamp = correctedTimestamp,
            Resolution = "1Day",
            SourceCount = 2,
            MatchedSourceCount = 2,
            Verified = true,
            ToleranceApplied = 0.001m,
            PrimarySourceValue = tiingoClose,
            ComputationVersion = 1,
            EvaluatedAt = DateTimeOffset.UtcNow
        });
        dbContext.Discrepancies.Add(new Discrepancy
        {
            Symbol = ticker,
            Timestamp = correctedTimestamp,
            FieldOrBarType = "Close",
            SourceA = SourceType.Alpaca,
            ValueA = alpacaClose,
            SourceB = SourceType.Tiingo,
            ValueB = tiingoClose,
            PercentDiff = Math.Abs(alpacaClose - tiingoClose) / alpacaClose,
            ResolutionMethod = DiscrepancyResolutionMethod.OhlAgreedCloseResolvedToTiingo,
            DetectedAt = DateTimeOffset.UtcNow
        });

        SeedUniverse(dbContext, correctedDate, ticker);
        await dbContext.SaveChangesAsync();

        var rollingService = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await rollingService.RunAsync(correctedDate, CancellationToken.None);

        var dmaService = new DmaComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<DmaComputationService>.Instance);
        await dmaService.ComputeAsync(ticker, TickerSourceOrigin.Watchlist, CancellationToken.None);

        var rollingTerm5 = await dbContext.RollingDmaStates.SingleAsync(s => s.Ticker == ticker && s.Term == 5);
        var gateTerm5 = await dbContext.DmaIndicators.SingleAsync(d => d.Symbol == ticker && d.Window == 5);

        // avg(200,202,204,206,210) = 204.4 - the corrected Close, NOT Alpaca's raw 300 (which
        // would have produced avg(200,202,204,206,300) = 222.4, the exact bug this fix closes).
        Assert.Equal(204.4m, rollingTerm5.Value);
        Assert.Equal(gateTerm5.Value, rollingTerm5.Value);
    }

    [Fact]
    public async Task RunAsync_TradingCalendarGapDetected_ForcesFullRecomputeRatherThanIncrementalMath()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var closes = Enumerable.Range(1, 5).Select(i => (decimal)i).ToList(); // days 0-4, closes 1..5
        var day1SnapshotDate = FirstDay.AddDays(4);
        SeedBars(dbContext, "GAPCO", closes);
        SeedUniverse(dbContext, day1SnapshotDate, "GAPCO");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(day1SnapshotDate, CancellationToken.None); // bootstrap: term5 = avg(1..5) = 3

        // Day offset 5 is skipped entirely (no bar) - the next bar lands at offset 6, close=7.
        var gapAffectedDate = FirstDay.AddDays(6);
        dbContext.RawOhlcvBars.Add(MakeBar("GAPCO", gapAffectedDate, 7m));
        SeedUniverse(dbContext, gapAffectedDate, "GAPCO");
        await dbContext.SaveChangesAsync();

        // Calendar confirms offset 5 was a real open trading day - a genuine gap, not a
        // weekend/holiday closure.
        var openDays = Enumerable.Range(0, 7).Select(o => FirstDay.AddDays(o)).ToHashSet();
        var calendar = new FakeTradingCalendarService(openDays);
        var gapService = new RollingDmaStateService(dbContext, calendar, NullLogger<RollingDmaStateService>.Instance);

        await gapService.RunAsync(gapAffectedDate, CancellationToken.None);

        var term5 = await dbContext.RollingDmaStates.SingleAsync(s => s.Ticker == "GAPCO" && s.Term == 5);
        // From-scratch recompute over the actually-available bars [1,2,3,4,5,7] (6 closes) -
        // trailing 5 = [2,3,4,5,7], avg = 4.2. A naive incremental drop/add across the gap
        // (dropping close=1, adding close=7 onto the old sum of 15) would have wrongly produced
        // (15 - 1 + 7)/5 = 4.2 as well by coincidence here, so the real proof is UpdatesSinceAnchor
        // resetting to 0 (a re-anchor-style recompute occurred) rather than incrementing to 1.
        Assert.Equal(4.2m, term5.Value);
        Assert.Equal(0, term5.UpdatesSinceAnchor);
    }

    [Fact]
    public async Task RunAsync_ReanchorDue_DiscardsDriftedRollingSumAndRecomputesFromRealBars()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var closes = Enumerable.Range(1, 61).Select(i => (decimal)i).ToList(); // 1..61
        var priorDate = FirstDay.AddDays(59); // through close=60 (60 bars: offsets 0..59)
        var newBarDate = FirstDay.AddDays(60); // close=61

        SeedBars(dbContext, "DRIFT", closes);
        SeedUniverse(dbContext, newBarDate, "DRIFT");

        // Hand-seed all four terms with a deliberately WRONG rolling sum and 29 updates since
        // the last anchor (one more update triggers the fixed 30-night re-anchor). If the
        // service trusted this drifted sum instead of recomputing from scratch, the resulting
        // Value would reflect the bogus 999999 baseline instead of the real bar history.
        foreach (var term in new[] { 5, 15, 30, 60 })
        {
            dbContext.RollingDmaStates.Add(new RollingDmaState
            {
                Ticker = "DRIFT",
                Term = term,
                RollingSum = 999999m,
                BarCount = term,
                Value = 999999m / term,
                InsufficientHistory = false,
                LastBarDate = priorDate,
                IsAligned = false,
                UpdatesSinceAnchor = RollingDmaStateService.ReanchorIntervalNights - 1,
                CalculationVersion = RollingDmaStateService.CalculationVersion,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        await dbContext.SaveChangesAsync();

        var logger = new CapturingLogger<RollingDmaStateService>();
        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), logger);
        await service.RunAsync(newBarDate, CancellationToken.None);

        var states = await dbContext.RollingDmaStates.Where(s => s.Ticker == "DRIFT").ToListAsync();
        var byTerm = states.ToDictionary(s => s.Term);

        Assert.Equal(59m, byTerm[5].Value); // avg(57..61), NOT influenced by the drifted 999999 sum
        Assert.Equal(54m, byTerm[15].Value); // avg(47..61)
        Assert.Equal(46.5m, byTerm[30].Value); // avg(32..61)
        Assert.Equal(31.5m, byTerm[60].Value); // avg(2..61)

        foreach (var state in states)
        {
            Assert.Equal(0, state.UpdatesSinceAnchor); // reset after the re-anchor
        }

        Assert.Contains(logger.Messages, m => m.Contains("re-anchor"));
    }

    [Fact]
    public async Task RunAsync_FirstRunEver_LogsBootstrapPassBanner()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var snapshotDate = FirstDay;
        SeedBars(dbContext, "AAPL", new List<decimal> { 100m });
        SeedUniverse(dbContext, snapshotDate, "AAPL");
        await dbContext.SaveChangesAsync();

        var logger = new CapturingLogger<RollingDmaStateService>();
        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), logger);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        Assert.Contains(logger.Messages, m => m.Contains("BOOTSTRAP PASS"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("STEADY-STATE"));
    }

    [Fact]
    public async Task RunAsync_SubsequentRun_LogsSteadyStateIncrementalPassBanner()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var day1 = FirstDay;
        var day2 = FirstDay.AddDays(1);
        SeedBars(dbContext, "AAPL", new List<decimal> { 100m });
        SeedUniverse(dbContext, day1, "AAPL");
        await dbContext.SaveChangesAsync();

        var firstLogger = new CapturingLogger<RollingDmaStateService>();
        var firstService = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), firstLogger);
        await firstService.RunAsync(day1, CancellationToken.None);

        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", day2, 101m));
        SeedUniverse(dbContext, day2, "AAPL");
        await dbContext.SaveChangesAsync();

        var secondLogger = new CapturingLogger<RollingDmaStateService>();
        var secondService = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), secondLogger);
        await secondService.RunAsync(day2, CancellationToken.None);

        Assert.Contains(secondLogger.Messages, m => m.Contains("STEADY-STATE INCREMENTAL PASS"));
        Assert.DoesNotContain(secondLogger.Messages, m => m.Contains("BOOTSTRAP PASS"));
    }

    [Fact]
    public async Task RunAsync_KnownPartialCoverage_LogsWarningButStillProcessesPresentTickersUnchanged()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var snapshotDate = FirstDay;
        SeedBars(dbContext, "AAPL", new List<decimal> { 100m });
        SeedUniverse(dbContext, snapshotDate, "AAPL");
        dbContext.UniverseSnapshotCoverages.Add(new UniverseSnapshotCoverage
        {
            SnapshotDate = snapshotDate,
            SourcesSucceeded = UniverseSourceCoverage.Nasdaq,
            RecordedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var logger = new CapturingLogger<RollingDmaStateService>();
        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), logger);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        // Visibility only, not a behavior gate - AAPL (the ticker actually present) is still
        // bootstrapped exactly as a full-coverage day would produce.
        Assert.Equal(4, await dbContext.RollingDmaStates.CountAsync());

        Assert.Contains(logger.Messages, m => m.Contains("KNOWN-PARTIAL") && m.Contains("Nasdaq"));
    }

    [Fact]
    public async Task RunAsync_FullCoverage_DoesNotLogPartialWarning()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var snapshotDate = FirstDay;
        SeedBars(dbContext, "AAPL", new List<decimal> { 100m });
        SeedUniverse(dbContext, snapshotDate, "AAPL");
        dbContext.UniverseSnapshotCoverages.Add(new UniverseSnapshotCoverage
        {
            SnapshotDate = snapshotDate,
            SourcesSucceeded = UniverseSourceCoverage.All,
            RecordedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var logger = new CapturingLogger<RollingDmaStateService>();
        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), logger);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        Assert.DoesNotContain(logger.Messages, m => m.Contains("KNOWN-PARTIAL"));
    }

    [Fact]
    public async Task RunAsync_NoCoverageRowForDate_DoesNotLogPartialWarningAndStillProcesses()
    {
        // A date predating this feature (or any other reason a coverage row was never written)
        // must fail open to the existing behavior - no warning, processing proceeds normally.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var snapshotDate = FirstDay;
        SeedBars(dbContext, "AAPL", new List<decimal> { 100m });
        SeedUniverse(dbContext, snapshotDate, "AAPL");
        await dbContext.SaveChangesAsync();

        var logger = new CapturingLogger<RollingDmaStateService>();
        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), logger);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        Assert.Equal(4, await dbContext.RollingDmaStates.CountAsync());
        Assert.DoesNotContain(logger.Messages, m => m.Contains("KNOWN-PARTIAL"));
    }

    [Fact]
    public async Task RunAsync_CalendarFetchFails_ProcessesNothingAndDoesNotThrow()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var snapshotDate = FirstDay;
        SeedBars(dbContext, "AAPL", new List<decimal> { 100m });
        SeedUniverse(dbContext, snapshotDate, "AAPL");
        await dbContext.SaveChangesAsync();

        var failingCalendar = FakeTradingCalendarService.ThatFails(new HttpRequestException("simulated Alpaca outage"));
        var service = new RollingDmaStateService(dbContext, failingCalendar, NullLogger<RollingDmaStateService>.Instance);

        await service.RunAsync(snapshotDate, CancellationToken.None);

        Assert.Empty(dbContext.RollingDmaStates);
    }

    [Fact]
    public async Task GetFullyAlignedTickersAsync_TickerFullyAligned_IsReturned()
    {
        // CLAUDE.md's "Calculator Ticker Source: Watchlist + Discovered-Aligned Union, Not a
        // Swap" (2026-07-30) - the discovery half of the union. Same ascending-closes fixture
        // as RunAsync_FirstRun_BootstrapsAllFourTermsAndEmitsNoCrossingLogEntries above, which
        // already establishes this produces a fully aligned (FullStackAligned = true) ticker.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var closes = Enumerable.Range(1, 64).Select(i => (decimal)i).ToList();
        var snapshotDate = FirstDay.AddDays(closes.Count - 1);
        SeedBars(dbContext, "AAPL", closes);
        SeedUniverse(dbContext, snapshotDate, "AAPL");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        var aligned = await service.GetFullyAlignedTickersAsync(CancellationToken.None);

        Assert.Equal(new[] { "AAPL" }, aligned);
    }

    [Fact]
    public async Task GetFullyAlignedTickersAsync_TickerNotFullyAligned_IsExcluded()
    {
        // Descending closes: shorter-window trailing averages are LOWER than longer-window
        // ones, the opposite of alignment - FullStackAligned must stay false.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var closes = Enumerable.Range(1, 64).Select(i => (decimal)(65 - i)).ToList();
        var snapshotDate = FirstDay.AddDays(closes.Count - 1);
        SeedBars(dbContext, "DESC", closes);
        SeedUniverse(dbContext, snapshotDate, "DESC");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        var aligned = await service.GetFullyAlignedTickersAsync(CancellationToken.None);

        Assert.Empty(aligned);
    }

    [Fact]
    public async Task GetFullyAlignedTickersAsync_InsufficientHistory_IsExcluded()
    {
        // Ascending closes, but too few bars for any window to have a real Value yet -
        // IsAligned must never be true off a null Value, so this ticker cannot be "discovered."
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var closes = new List<decimal> { 10m, 20m, 30m };
        var snapshotDate = FirstDay.AddDays(closes.Count - 1);
        SeedBars(dbContext, "NEWCO", closes);
        SeedUniverse(dbContext, snapshotDate, "NEWCO");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        var aligned = await service.GetFullyAlignedTickersAsync(CancellationToken.None);

        Assert.Empty(aligned);
    }

    [Fact]
    public async Task GetFullyAlignedTickersAsync_MultipleAlignedTickers_ReturnsAllInAlphabeticalOrder()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var closes = Enumerable.Range(1, 64).Select(i => (decimal)i).ToList();
        var snapshotDate = FirstDay.AddDays(closes.Count - 1);
        SeedBars(dbContext, "ZETA", closes);
        SeedBars(dbContext, "ALPHA", closes);
        SeedUniverse(dbContext, snapshotDate, "ZETA", "ALPHA");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        var aligned = await service.GetFullyAlignedTickersAsync(CancellationToken.None);

        Assert.Equal(new[] { "ALPHA", "ZETA" }, aligned);
    }

    [Fact]
    public async Task GetDma5AlignedTickersAsync_TickerTerm5Aligned_IsReturned()
    {
        // CLAUDE.md's "Universe-Wide Dynamic Discovery - Watchlist File Retired from
        // Production Path" (2026-08-05) - the DMA-5-only discovery source, replacing
        // FullStackAligned to match Gate's actual Tier 1 entry gate. Same ascending-closes
        // fixture as GetFullyAlignedTickersAsync_TickerFullyAligned_IsReturned above, which
        // already establishes this produces a Term5-aligned (and, incidentally,
        // FullStackAligned) ticker.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var closes = Enumerable.Range(1, 64).Select(i => (decimal)i).ToList();
        var snapshotDate = FirstDay.AddDays(closes.Count - 1);
        SeedBars(dbContext, "AAPL", closes);
        SeedUniverse(dbContext, snapshotDate, "AAPL");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        var aligned = await service.GetDma5AlignedTickersAsync(CancellationToken.None);

        Assert.Equal(new[] { "AAPL" }, aligned);
    }

    [Fact]
    public async Task GetDma5AlignedTickersAsync_TickerNotTerm5Aligned_IsExcluded()
    {
        // Descending closes: Term5's own trailing average is LOWER than Term15's, the
        // opposite of alignment - Term5.IsAligned must stay false.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var closes = Enumerable.Range(1, 64).Select(i => (decimal)(65 - i)).ToList();
        var snapshotDate = FirstDay.AddDays(closes.Count - 1);
        SeedBars(dbContext, "DESC", closes);
        SeedUniverse(dbContext, snapshotDate, "DESC");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        var aligned = await service.GetDma5AlignedTickersAsync(CancellationToken.None);

        Assert.Empty(aligned);
    }

    [Fact]
    public async Task GetDma5AlignedTickersAsync_InsufficientHistory_IsExcluded()
    {
        // Ascending closes, but too few bars for Term5 to have a real Value yet -
        // IsAligned must never be true off a null Value, so this ticker cannot be "discovered."
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var closes = new List<decimal> { 10m, 20m, 30m };
        var snapshotDate = FirstDay.AddDays(closes.Count - 1);
        SeedBars(dbContext, "NEWCO", closes);
        SeedUniverse(dbContext, snapshotDate, "NEWCO");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        var aligned = await service.GetDma5AlignedTickersAsync(CancellationToken.None);

        Assert.Empty(aligned);
    }

    [Fact]
    public async Task GetDma5AlignedTickersAsync_MultipleAlignedTickers_ReturnsAllInAlphabeticalOrder()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var closes = Enumerable.Range(1, 64).Select(i => (decimal)i).ToList();
        var snapshotDate = FirstDay.AddDays(closes.Count - 1);
        SeedBars(dbContext, "ZETA", closes);
        SeedBars(dbContext, "ALPHA", closes);
        SeedUniverse(dbContext, snapshotDate, "ZETA", "ALPHA");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        var aligned = await service.GetDma5AlignedTickersAsync(CancellationToken.None);

        Assert.Equal(new[] { "ALPHA", "ZETA" }, aligned);
    }

    [Fact]
    public async Task GetDma5AlignedTickersAsync_Term5AlignedButFullStackNot_IsReturnedUnlikeFullyAlignedVariant()
    {
        // The actual reason this new method exists (CLAUDE.md, 2026-08-05): Term5 can be
        // aligned (DMA5 > DMA15) while the wider stack is not (DMA15 is NOT > DMA30), since
        // Term5 is an independent pairwise comparison, not gated on the other three - see
        // RollingDmaAlignmentEvaluator. A ticker in this shape is discoverable via
        // GetDma5AlignedTickersAsync but NOT via GetFullyAlignedTickersAsync, proving the two
        // methods use genuinely different, not merely differently-named, criteria.
        //
        // Oldest 50 closes at 100 (baseline), then 10 closes dipped to 50, then the newest 5
        // closes spiked to 110. Trailing-window math (DMA-N = avg of the last N closes):
        //   DMA5  = avg(110 x5)                          = 110
        //   DMA15 = avg(50 x10, 110 x5)                   = 1050/15 = 70
        //   DMA30 = avg(100 x15, 50 x10, 110 x5)           = 2550/30 = 85
        //   DMA60 = avg(100 x45, 50 x10, 110 x5)           = 5550/60 = 92.5
        // Term5: 110 > 70 -> true. Term15: 70 > 85 -> false (breaks the AND immediately, so
        // FullStackAligned is false regardless of Term30). This is not the "nested sensitivity"
        // shape a single spike-from-flat baseline would produce (which aligns all four terms at
        // once, per RunAsync_SpikeAfterFlatBaseline... above) - the intervening low-value
        // segment is what isolates Term5 from the wider stack.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        var closes = Enumerable.Repeat(100m, 50)
            .Concat(Enumerable.Repeat(50m, 10))
            .Concat(Enumerable.Repeat(110m, 5))
            .ToList();
        var snapshotDate = FirstDay.AddDays(closes.Count - 1);
        SeedBars(dbContext, "TAILCO", closes);
        SeedUniverse(dbContext, snapshotDate, "TAILCO");
        await dbContext.SaveChangesAsync();

        var service = new RollingDmaStateService(dbContext, new FakeTradingCalendarService(), NullLogger<RollingDmaStateService>.Instance);
        await service.RunAsync(snapshotDate, CancellationToken.None);

        var states = await dbContext.RollingDmaStates.Where(s => s.Ticker == "TAILCO").ToListAsync();
        var byTerm = states.ToDictionary(s => s.Term);
        Assert.Equal(110m, byTerm[5].Value);
        Assert.Equal(70m, byTerm[15].Value);
        Assert.Equal(85m, byTerm[30].Value);
        Assert.Equal(92.5m, byTerm[60].Value);
        Assert.True(byTerm[5].IsAligned);
        Assert.False(byTerm[60].IsAligned); // FullStackAligned - confirms the fixture actually isolates the two conditions

        var dma5Aligned = await service.GetDma5AlignedTickersAsync(CancellationToken.None);
        var fullyAligned = await service.GetFullyAlignedTickersAsync(CancellationToken.None);

        Assert.Equal(new[] { "TAILCO" }, dma5Aligned);
        Assert.Empty(fullyAligned);
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
            lock (_lock)
            {
                _messages.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
