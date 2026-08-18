using Drps.Calculator.Rvol;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Calculator;

public class RvolComputationServiceTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    private static RawOhlcvBar MakeBar(string symbol, DateTimeOffset timestamp, long volume) => new()
    {
        Source = SourceType.Alpaca,
        Symbol = symbol,
        Timestamp = timestamp,
        Resolution = "1Day",
        Open = 99m,
        High = 101m,
        Low = 98m,
        Close = 100m,
        Volume = volume,
        AdjustmentType = "raw",
        IngestedAt = DateTimeOffset.UtcNow,
        RequestId = Guid.NewGuid()
    };

    private static BarVerification MakeVerification(string symbol, DateTimeOffset timestamp, bool verified) => new()
    {
        Symbol = symbol,
        Timestamp = timestamp,
        Resolution = "1Day",
        SourceCount = verified ? 2 : 1,
        MatchedSourceCount = verified ? 2 : 1,
        Verified = verified,
        ToleranceApplied = 0.001m,
        ComputationVersion = 1,
        EvaluatedAt = DateTimeOffset.UtcNow
    };

    // 20 bars of constant volume 1000, then a 21st bar spiking to 2500 -> RVOL = 2.5 exact
    // (same hand-verified sequence as RvolCalculatorTests).
    private static void SeedBars(Drps.Calculator.Persistence.CalculatorDbContext dbContext, string symbol, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var volume = i < 20 ? 1000L : 2500L;
            var timestamp = new DateTimeOffset(FirstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar(symbol, timestamp, volume));
            dbContext.BarVerifications.Add(MakeVerification(symbol, timestamp, verified: true));
        }
    }

    [Fact]
    public async Task ComputeAsync_TwentyOneBars_InsertsOneRvolRowMatchingHandCalculatedValue()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 21);
        await dbContext.SaveChangesAsync();

        var service = new RvolComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RvolComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.RvolIndicators.SingleAsync();
        Assert.Equal(20, indicator.BaselineWindow);
        Assert.Equal(2.5m, indicator.Value); // hand-calculated in RvolCalculatorTests
        Assert.Equal(RvolComputationService.CalculationVersion, indicator.CalculationVersion);
        Assert.False(indicator.HasExDividendEvent);
    }

    [Theory]
    [InlineData(TickerSourceOrigin.Watchlist)]
    [InlineData(TickerSourceOrigin.DiscoveredAligned)]
    [InlineData(TickerSourceOrigin.Both)]
    public async Task ComputeAsync_GivenSourceOrigin_StampsItOnEveryNewlyAddedRow(TickerSourceOrigin sourceOrigin)
    {
        // CLAUDE.md's "Calculator Ticker Source: Watchlist + Discovered-Aligned Union, Not a
        // Swap" (2026-07-30) - Worker.cs resolves origin once per symbol and passes it in; this
        // proves ComputeAsync faithfully stamps whatever it's given onto every new RVOL row.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 30); // baseline window 20 -> multiple RVOL rows produced
        await dbContext.SaveChangesAsync();

        var service = new RvolComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RvolComputationService>.Instance);
        await service.ComputeAsync("AAPL", sourceOrigin, CancellationToken.None);

        var indicators = await dbContext.RvolIndicators.ToListAsync();
        Assert.NotEmpty(indicators);
        Assert.All(indicators, i => Assert.Equal(sourceOrigin, i.TickerSourceOrigin));
    }

    [Fact]
    public async Task ComputeAsync_TwentyBars_InsertsNothing()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 20); // one short of the 21 bars RVOL requires

        var service = new RvolComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RvolComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        Assert.Empty(dbContext.RvolIndicators);
    }

    [Fact]
    public async Task ComputeAsync_CalledTwiceForSameData_DoesNotInsertDuplicateRows()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 21);
        await dbContext.SaveChangesAsync();

        var service = new RvolComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RvolComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);
        var countAfterFirstRun = await dbContext.RvolIndicators.CountAsync();

        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);
        var countAfterSecondRun = await dbContext.RvolIndicators.CountAsync();

        Assert.Equal(countAfterFirstRun, countAfterSecondRun);
    }

    [Fact]
    public async Task ComputeAsync_CalendarGapInsideWindow_SkipsTheResult()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // 21 bars exist, but day offset 3 has no bar at all - a real ingestion gap inside
        // RVOL's 21-bar window.
        var barDayOffsets = Enumerable.Range(0, 3).Concat(Enumerable.Range(4, 19)).ToList(); // skips offset 3, 22 total
        for (var i = 0; i < barDayOffsets.Count; i++)
        {
            var offset = barDayOffsets[i];
            var timestamp = new DateTimeOffset(FirstDay.AddDays(offset).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 1000L));
            dbContext.BarVerifications.Add(MakeVerification("AAPL", timestamp, verified: true));
        }
        await dbContext.SaveChangesAsync();

        // Calendar confirms day offset 3 was a real open trading day.
        var openDays = Enumerable.Range(0, 23).Select(o => FirstDay.AddDays(o)).ToHashSet();

        var service = new RvolComputationService(dbContext, new FakeTradingCalendarService(openDays), NullLogger<RvolComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        Assert.Empty(dbContext.RvolIndicators);
    }

    [Fact]
    public async Task ComputeAsync_ExDividendDateInsideWindow_SetsHasExDividendEventTrue()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 21); // window span: FirstDay through FirstDay+20
        dbContext.RawExDividendObservations.Add(new RawExDividendObservation
        {
            Source = SourceType.Finnhub,
            Symbol = "AAPL",
            ExDividendDate = FirstDay.AddDays(10), // inside the window's span
            Value = 0.24m,
            SampleCount = 1,
            Verified = false,
            IngestedAt = DateTimeOffset.UtcNow,
            RequestId = Guid.NewGuid()
        });
        await dbContext.SaveChangesAsync();

        var service = new RvolComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RvolComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.RvolIndicators.SingleAsync();
        Assert.True(indicator.HasExDividendEvent);
        // Informational only - the value itself is untouched by the ex-div event.
        Assert.Equal(2.5m, indicator.Value);
    }

    [Fact]
    public async Task ComputeAsync_ExDividendDateOutsideWindow_SetsHasExDividendEventFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 30); // multiple results; window for bars[20] spans day0-day20
        dbContext.RawExDividendObservations.Add(new RawExDividendObservation
        {
            Source = SourceType.Finnhub,
            Symbol = "AAPL",
            ExDividendDate = FirstDay.AddDays(25), // outside the first result's window
            Value = 0.24m,
            SampleCount = 1,
            Verified = false,
            IngestedAt = DateTimeOffset.UtcNow,
            RequestId = Guid.NewGuid()
        });
        await dbContext.SaveChangesAsync();

        var service = new RvolComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RvolComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var firstIndicator = await dbContext.RvolIndicators.SingleAsync(r => r.BarDate == FirstDay.AddDays(20));
        Assert.False(firstIndicator.HasExDividendEvent);
    }

    [Fact]
    public async Task ComputeAsync_NoBarsAtAll_InsertsNothing()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        var service = new RvolComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RvolComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        Assert.Empty(dbContext.RvolIndicators);
    }

    [Fact]
    public async Task ComputeAsync_CorrectedBar_SetsProvenanceFlagButValueIsUnaffected()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 21);

        // RVOL's own Value is Volume-based and is never substituted (only Close is ever
        // corrected by the OHL-agreed exception) - a Close correction on the spike day still
        // sets the provenance flag, but the 2.5x RVOL result must be completely unaffected.
        var spikeDay = new DateTimeOffset(FirstDay.AddDays(20).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.BarVerifications.Add(new BarVerification
        {
            Symbol = "AAPL",
            Timestamp = spikeDay,
            Resolution = "1Day",
            SourceCount = 2,
            MatchedSourceCount = 2,
            Verified = true,
            ToleranceApplied = 0.001m,
            PrimarySourceValue = 105m,
            ComputationVersion = 1,
            EvaluatedAt = DateTimeOffset.UtcNow
        });
        dbContext.Discrepancies.Add(new Discrepancy
        {
            Symbol = "AAPL",
            Timestamp = spikeDay,
            FieldOrBarType = "Close",
            SourceA = SourceType.Alpaca,
            ValueA = 100m,
            SourceB = SourceType.Tiingo,
            ValueB = 105m,
            PercentDiff = 0.05m,
            ResolutionMethod = DiscrepancyResolutionMethod.OhlAgreedCloseResolvedToTiingo,
            DetectedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var service = new RvolComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RvolComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.RvolIndicators.SingleAsync();
        Assert.Equal(2.5m, indicator.Value); // unchanged - RVOL never reads Close at all
        Assert.True(indicator.HasTiingoCorrectedClose);
    }

    [Fact]
    public async Task ComputeAsync_OrdinaryUncorrectedBar_ProvenanceFlagFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 21); // no Discrepancy/correction rows at all
        await dbContext.SaveChangesAsync();

        var service = new RvolComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RvolComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.RvolIndicators.SingleAsync();
        Assert.Equal(2.5m, indicator.Value);
        Assert.False(indicator.HasTiingoCorrectedClose);
    }

    [Fact]
    public async Task ComputeAsync_Jpm20260626RealValues_ProvenanceFlagSetValueUnaffected()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "JPM", 21);

        // Real values from this session's audit: JPM 2026-06-26 - Alpaca printed 327.50
        // (confirmed incorrect against real market data), Tiingo printed 329.05 (confirmed
        // correct). Applied to the spike day (offset 20) purely to exercise the real
        // ticker/date/value combination - RVOL's own Value must stay keyed to real Volume,
        // completely unaffected by this Close correction.
        var jun26 = new DateTimeOffset(FirstDay.AddDays(20).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.BarVerifications.Add(new BarVerification
        {
            Symbol = "JPM",
            Timestamp = jun26,
            Resolution = "1Day",
            SourceCount = 2,
            MatchedSourceCount = 2,
            Verified = true,
            ToleranceApplied = 0.001m,
            PrimarySourceValue = 329.05m,
            ComputationVersion = 1,
            EvaluatedAt = DateTimeOffset.UtcNow
        });
        dbContext.Discrepancies.Add(new Discrepancy
        {
            Symbol = "JPM",
            Timestamp = jun26,
            FieldOrBarType = "Close",
            SourceA = SourceType.Alpaca,
            ValueA = 327.50m,
            SourceB = SourceType.Tiingo,
            ValueB = 329.05m,
            PercentDiff = Math.Abs(327.50m - 329.05m) / 327.50m,
            ResolutionMethod = DiscrepancyResolutionMethod.OhlAgreedCloseResolvedToTiingo,
            DetectedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var service = new RvolComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RvolComputationService>.Instance);
        await service.ComputeAsync("JPM", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.RvolIndicators.SingleAsync();
        Assert.Equal(2.5m, indicator.Value); // Volume-based, unaffected by the Close correction
        Assert.True(indicator.HasTiingoCorrectedClose);
    }
}
