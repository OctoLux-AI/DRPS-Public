using Drps.Calculator.Dma;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Calculator;

public class DmaComputationServiceTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

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

    private static BarVerification MakeVerification(string symbol, DateTimeOffset timestamp, bool verified) => new()
    {
        Symbol = symbol,
        Timestamp = timestamp,
        Resolution = "1Day",
        SourceCount = 2,
        MatchedSourceCount = verified ? 2 : 1,
        Verified = verified,
        ToleranceApplied = 0.001m,
        ComputationVersion = 1,
        EvaluatedAt = DateTimeOffset.UtcNow
    };

    private static void SeedVerifiedBars(Drps.Calculator.Persistence.CalculatorDbContext dbContext, string symbol, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var timestamp = new DateTimeOffset(FirstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar(symbol, timestamp, i + 1m));
            dbContext.BarVerifications.Add(MakeVerification(symbol, timestamp, verified: true));
        }
    }

    // Marks (Symbol, Timestamp) as resolved via the narrow OHL-agreed/Close-resolved-to-Tiingo
    // exception (CLAUDE.md, 2026-07-17) - both the Discrepancy row (the actual signal
    // GetTiingoCorrectedClosesAsync queries) and a BarVerification row carrying the corrected
    // PrimarySourceValue, matching exactly what BarReconciliationService itself writes when
    // this exception fires.
    private static void SeedTiingoCorrectedBar(
        Drps.Calculator.Persistence.CalculatorDbContext dbContext,
        string symbol,
        DateTimeOffset timestamp,
        decimal alpacaClose,
        decimal tiingoClose)
    {
        dbContext.RawOhlcvBars.Add(MakeBar(symbol, timestamp, alpacaClose));
        dbContext.BarVerifications.Add(new BarVerification
        {
            Symbol = symbol,
            Timestamp = timestamp,
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
            Symbol = symbol,
            Timestamp = timestamp,
            FieldOrBarType = "Close",
            SourceA = SourceType.Alpaca,
            ValueA = alpacaClose,
            SourceB = SourceType.Tiingo,
            ValueB = tiingoClose,
            PercentDiff = Math.Abs(alpacaClose - tiingoClose) / alpacaClose,
            ResolutionMethod = DiscrepancyResolutionMethod.OhlAgreedCloseResolvedToTiingo,
            DetectedAt = DateTimeOffset.UtcNow
        });
    }

    [Fact]
    public async Task ComputeAsync_FiveVerifiedBars_InsertsExactlyOneDma5Row()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedVerifiedBars(dbContext, "AAPL", 5);
        await dbContext.SaveChangesAsync();

        var service = new DmaComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<DmaComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.DmaIndicators.SingleAsync();
        Assert.Equal(5, indicator.Window);
        Assert.Equal(3m, indicator.Value); // avg(1..5)
        Assert.Equal(DmaComputationService.CalculationVersion, indicator.CalculationVersion);
    }

    [Theory]
    [InlineData(TickerSourceOrigin.Watchlist)]
    [InlineData(TickerSourceOrigin.DiscoveredAligned)]
    [InlineData(TickerSourceOrigin.Both)]
    public async Task ComputeAsync_GivenSourceOrigin_StampsItOnEveryNewlyAddedRow(TickerSourceOrigin sourceOrigin)
    {
        // CLAUDE.md's "Calculator Ticker Source: Watchlist + Discovered-Aligned Union, Not a
        // Swap" (2026-07-30) - Worker.cs resolves origin once per symbol and passes it in; this
        // proves ComputeAsync faithfully stamps whatever it's given onto every new row for all
        // four DMA windows this bar count actually produces, not just the first one.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedVerifiedBars(dbContext, "AAPL", 60); // enough bars for all four windows (5/15/30/60)
        await dbContext.SaveChangesAsync();

        var service = new DmaComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<DmaComputationService>.Instance);
        await service.ComputeAsync("AAPL", sourceOrigin, CancellationToken.None);

        var indicators = await dbContext.DmaIndicators.ToListAsync();
        Assert.NotEmpty(indicators);
        Assert.All(indicators, i => Assert.Equal(sourceOrigin, i.TickerSourceOrigin));
    }

    [Fact]
    public async Task ComputeAsync_UnverifiedBarStillCountsTowardWindow_ComputesRegardlessOfVerificationStatus()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedVerifiedBars(dbContext, "AAPL", 4); // 4 verified bars

        // A 5th day exists in RawOhlcvBars but failed cross-source verification. Per the
        // fail-closed-at-read-time redesign, computation no longer gates on verification
        // status at all - this bar must still count toward the window, same as a verified
        // one would. (DmaVerificationJoinServiceTests covers the read-time trust check.)
        var unverifiedTimestamp = new DateTimeOffset(FirstDay.AddDays(4).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", unverifiedTimestamp, 5m));
        dbContext.BarVerifications.Add(MakeVerification("AAPL", unverifiedTimestamp, verified: false));
        await dbContext.SaveChangesAsync();

        var service = new DmaComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<DmaComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.DmaIndicators.SingleAsync();
        Assert.Equal(5, indicator.Window);
        Assert.Equal(3m, indicator.Value); // avg(1..5)
    }

    [Fact]
    public async Task ComputeAsync_BarWithNoVerificationRowAtAll_StillCountsTowardWindow()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedVerifiedBars(dbContext, "AAPL", 4); // 4 verified bars

        // A 5th day's raw bar exists but reconciliation hasn't run for it yet - no
        // BarVerification row at all. Must still count toward the window; only the
        // read-time join service treats a missing verification row as unverified.
        var timestamp = new DateTimeOffset(FirstDay.AddDays(4).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 5m));
        await dbContext.SaveChangesAsync();

        var service = new DmaComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<DmaComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.DmaIndicators.SingleAsync();
        Assert.Equal(5, indicator.Window);
        Assert.Equal(3m, indicator.Value); // avg(1..5)
    }

    [Fact]
    public async Task ComputeAsync_NoBarsAtAll_InsertsNothing()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        var service = new DmaComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<DmaComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        Assert.Empty(dbContext.DmaIndicators);
    }

    [Fact]
    public async Task ComputeAsync_CalledTwiceForSameData_DoesNotInsertDuplicateRows()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedVerifiedBars(dbContext, "AAPL", 8);
        await dbContext.SaveChangesAsync();

        var service = new DmaComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<DmaComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);
        var countAfterFirstRun = await dbContext.DmaIndicators.CountAsync();

        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);
        var countAfterSecondRun = await dbContext.DmaIndicators.CountAsync();

        Assert.Equal(countAfterFirstRun, countAfterSecondRun);
    }

    [Fact]
    public async Task ComputeAsync_NewBarAppearsAfterFirstRun_OnlyAddsTheNewlyEligibleRows()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedVerifiedBars(dbContext, "AAPL", 5);
        await dbContext.SaveChangesAsync();

        var service = new DmaComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<DmaComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);
        var countAfterFirstRun = await dbContext.DmaIndicators.CountAsync();

        // A 6th verified day lands before the next scheduled run.
        var sixthDayTimestamp = new DateTimeOffset(FirstDay.AddDays(5).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", sixthDayTimestamp, 6m));
        dbContext.BarVerifications.Add(MakeVerification("AAPL", sixthDayTimestamp, verified: true));
        await dbContext.SaveChangesAsync();
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);
        var countAfterSecondRun = await dbContext.DmaIndicators.CountAsync();

        Assert.Equal(countAfterFirstRun + 1, countAfterSecondRun); // one new DMA-5 value only
    }

    [Fact]
    public async Task ComputeAsync_CalendarReportsGapInsideOnlyEligibleWindow_SkipsThatWindow()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // 5 verified bars exist, but day offset 2 has no bar at all - actual bar dates are
        // offsets 0, 1, 3, 4, 5. DMA-5 needs exactly 5 bars and is the only window with
        // enough bars here.
        var barDayOffsets = new[] { 0, 1, 3, 4, 5 };
        foreach (var offset in barDayOffsets)
        {
            var timestamp = new DateTimeOffset(FirstDay.AddDays(offset).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, offset + 1m));
            dbContext.BarVerifications.Add(MakeVerification("AAPL", timestamp, verified: true));
        }
        await dbContext.SaveChangesAsync();

        // Calendar confirms day offset 2 was a real open trading day - a genuine
        // ingestion/verification gap, not a weekend/holiday closure.
        var openDays = Enumerable.Range(0, 6).Select(o => FirstDay.AddDays(o)).ToHashSet();

        var service = new DmaComputationService(dbContext, new FakeTradingCalendarService(openDays), NullLogger<DmaComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        Assert.Empty(dbContext.DmaIndicators);
    }

    [Fact]
    public async Task ComputeAsync_ExDividendDateFallsWithinWindow_SetsHasExDividendEventTrue()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedVerifiedBars(dbContext, "AAPL", 5); // window span: FirstDay through FirstDay+4
        dbContext.RawExDividendObservations.Add(new RawExDividendObservation
        {
            Source = SourceType.Finnhub,
            Symbol = "AAPL",
            ExDividendDate = FirstDay.AddDays(2), // inside the DMA-5 window's span
            Value = 0.24m,
            SampleCount = 1,
            Verified = false,
            IngestedAt = DateTimeOffset.UtcNow,
            RequestId = Guid.NewGuid()
        });
        await dbContext.SaveChangesAsync();

        var service = new DmaComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<DmaComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.DmaIndicators.SingleAsync();
        Assert.True(indicator.HasExDividendEvent);
        // Informational only - the value itself is untouched by the ex-div event.
        Assert.Equal(3m, indicator.Value);
    }

    [Fact]
    public async Task ComputeAsync_ExDividendDateOutsideWindow_SetsHasExDividendEventFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedVerifiedBars(dbContext, "AAPL", 5); // window span: FirstDay through FirstDay+4
        dbContext.RawExDividendObservations.Add(new RawExDividendObservation
        {
            Source = SourceType.Finnhub,
            Symbol = "AAPL",
            ExDividendDate = FirstDay.AddDays(30), // well outside the window's span
            Value = 0.24m,
            SampleCount = 1,
            Verified = false,
            IngestedAt = DateTimeOffset.UtcNow,
            RequestId = Guid.NewGuid()
        });
        await dbContext.SaveChangesAsync();

        var service = new DmaComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<DmaComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.DmaIndicators.SingleAsync();
        Assert.False(indicator.HasExDividendEvent);
    }

    [Fact]
    public async Task ComputeAsync_NoExDividendObservationsAtAll_DefaultsHasExDividendEventFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedVerifiedBars(dbContext, "AAPL", 5);
        await dbContext.SaveChangesAsync();

        var service = new DmaComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<DmaComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.DmaIndicators.SingleAsync();
        Assert.False(indicator.HasExDividendEvent);
    }

    [Fact]
    public async Task ComputeAsync_CorrectedBar_UsesTiingoPrimarySourceValueAndSetsProvenanceFlag()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // 4 ordinary bars (Close = 1, 2, 3, 4) plus a 5th day whose Alpaca Close (100) was
        // resolved to Tiingo's corrected Close (10) via the narrow OHL-agreed exception.
        for (var i = 0; i < 4; i++)
        {
            var timestamp = new DateTimeOffset(FirstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, i + 1m));
            dbContext.BarVerifications.Add(MakeVerification("AAPL", timestamp, verified: true));
        }
        var correctedTimestamp = new DateTimeOffset(FirstDay.AddDays(4).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        SeedTiingoCorrectedBar(dbContext, "AAPL", correctedTimestamp, alpacaClose: 100m, tiingoClose: 10m);
        await dbContext.SaveChangesAsync();

        var service = new DmaComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<DmaComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.DmaIndicators.SingleAsync();
        // avg(1,2,3,4,10) = 4, not avg(1,2,3,4,100) = 22 - confirms the corrected Close, not
        // Alpaca's raw 100, fed the calculation.
        Assert.Equal(4m, indicator.Value);
        Assert.True(indicator.HasTiingoCorrectedClose);
    }

    [Fact]
    public async Task ComputeAsync_OrdinaryUncorrectedBar_ProvenanceFlagFalseAndValueMatchesAlpacaClose()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedVerifiedBars(dbContext, "AAPL", 5); // no Discrepancy/correction rows at all
        await dbContext.SaveChangesAsync();

        var service = new DmaComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<DmaComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.DmaIndicators.SingleAsync();
        Assert.Equal(3m, indicator.Value); // avg(1..5), Alpaca's raw Close, unchanged
        Assert.False(indicator.HasTiingoCorrectedClose);
    }

    [Fact]
    public async Task ComputeAsync_Jpm20260626RealValues_ComputesDma5FromTiingoCorrectedClose()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // Real values from this session's audit: JPM 2026-06-26 - Alpaca printed 327.50
        // (confirmed incorrect against real market data), Tiingo printed 329.05 (confirmed
        // correct), resolved via the OHL-agreed/Close-resolved-to-Tiingo exception.
        var precedingCloses = new[] { 325.00m, 326.00m, 328.00m, 330.00m };
        for (var i = 0; i < precedingCloses.Length; i++)
        {
            var timestamp = new DateTimeOffset(FirstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar("JPM", timestamp, precedingCloses[i]));
            dbContext.BarVerifications.Add(MakeVerification("JPM", timestamp, verified: true));
        }
        var jun26 = new DateTimeOffset(FirstDay.AddDays(4).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        SeedTiingoCorrectedBar(dbContext, "JPM", jun26, alpacaClose: 327.50m, tiingoClose: 329.05m);
        await dbContext.SaveChangesAsync();

        var service = new DmaComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<DmaComputationService>.Instance);
        await service.ComputeAsync("JPM", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.DmaIndicators.SingleAsync();
        // avg(325.00, 326.00, 328.00, 330.00, 329.05) = 327.61 - NOT avg(..., 327.50) = 327.30.
        Assert.Equal(327.61m, indicator.Value);
        Assert.True(indicator.HasTiingoCorrectedClose);
    }

    [Fact]
    public async Task ComputeAsync_CalendarFetchFails_InsertsNothingAndDoesNotThrow()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedVerifiedBars(dbContext, "AAPL", 8);
        await dbContext.SaveChangesAsync();

        var failingCalendar = FakeTradingCalendarService.ThatFails(new HttpRequestException("simulated Alpaca outage"));
        var service = new DmaComputationService(dbContext, failingCalendar, NullLogger<DmaComputationService>.Instance);

        // Fail-closed: a calendar-fetch failure must not fall back to the old gap-blind
        // behavior - nothing gets computed for this symbol this run, and the failure itself
        // must not propagate and take down the whole Worker loop (Worker's own per-symbol
        // isolation is a separate concern; this asserts ComputeAsync's own contract).
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        Assert.Empty(dbContext.DmaIndicators);
    }
}
