using Drps.Calculator.Atr;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Calculator;

public class AtrComputationServiceTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    private static RawOhlcvBar MakeBar(string symbol, DateTimeOffset timestamp, decimal high, decimal low, decimal close) => new()
    {
        Source = SourceType.Alpaca,
        Symbol = symbol,
        Timestamp = timestamp,
        Resolution = "1Day",
        Open = close,
        High = high,
        Low = low,
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
        SourceCount = verified ? 2 : 1,
        MatchedSourceCount = verified ? 2 : 1,
        Verified = verified,
        ToleranceApplied = 0.001m,
        ComputationVersion = 1,
        EvaluatedAt = DateTimeOffset.UtcNow
    };

    // Same hand-verified sequence as AtrCalculatorTests: 15 bars of steady up-drift with a
    // constant 2-point range -> ATR seed of exactly 2.
    private static void SeedBars(Drps.Calculator.Persistence.CalculatorDbContext dbContext, string symbol, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var timestamp = new DateTimeOffset(FirstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar(symbol, timestamp, 102m + i, 100m + i, 101m + i));
            dbContext.BarVerifications.Add(MakeVerification(symbol, timestamp, verified: true));
        }
    }

    // Marks (Symbol, Timestamp) as resolved via the narrow OHL-agreed/Close-resolved-to-Tiingo
    // exception (CLAUDE.md, 2026-07-17) - both the Discrepancy row (the actual signal
    // GetTiingoCorrectedClosesAsync queries) and a BarVerification row carrying the corrected
    // PrimarySourceValue, matching exactly what BarReconciliationService itself writes when
    // this exception fires. High/Low are passed through unchanged - only Close is ever
    // substituted (the OHL-agreed signature requires Open/High/Low to already agree).
    private static void SeedTiingoCorrectedBar(
        Drps.Calculator.Persistence.CalculatorDbContext dbContext,
        string symbol,
        DateTimeOffset timestamp,
        decimal high,
        decimal low,
        decimal alpacaClose,
        decimal tiingoClose)
    {
        dbContext.RawOhlcvBars.Add(MakeBar(symbol, timestamp, high, low, alpacaClose));
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
    public async Task ComputeAsync_FifteenBars_InsertsOneAtrRowMatchingHandCalculatedValue()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 15);
        await dbContext.SaveChangesAsync();

        var service = new AtrComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<AtrComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.AtrIndicators.SingleAsync();
        Assert.Equal(14, indicator.Period);
        Assert.Equal(2m, indicator.Value); // hand-calculated in AtrCalculatorTests
        Assert.Equal(AtrComputationService.CalculationVersion, indicator.CalculationVersion);
        Assert.False(indicator.HasExDividendEvent);
        Assert.True(indicator.VerificationScopeLimited);
    }

    [Theory]
    [InlineData(TickerSourceOrigin.Watchlist)]
    [InlineData(TickerSourceOrigin.DiscoveredAligned)]
    [InlineData(TickerSourceOrigin.Both)]
    public async Task ComputeAsync_GivenSourceOrigin_StampsItOnEveryNewlyAddedRow(TickerSourceOrigin sourceOrigin)
    {
        // CLAUDE.md's "Calculator Ticker Source: Watchlist + Discovered-Aligned Union, Not a
        // Swap" (2026-07-30) - Worker.cs resolves origin once per symbol and passes it in; this
        // proves ComputeAsync faithfully stamps whatever it's given onto every new ATR row.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 25);
        await dbContext.SaveChangesAsync();

        var service = new AtrComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<AtrComputationService>.Instance);
        await service.ComputeAsync("AAPL", sourceOrigin, CancellationToken.None);

        var indicators = await dbContext.AtrIndicators.ToListAsync();
        Assert.NotEmpty(indicators);
        Assert.All(indicators, i => Assert.Equal(sourceOrigin, i.TickerSourceOrigin));
    }

    [Fact]
    public async Task ComputeAsync_MultipleAtrRowsProduced_VerificationScopeLimitedIsTrueOnEveryRow()
    {
        // VerificationScopeLimited is a permanent disclaimer about this indicator type, not
        // a per-row computed condition - it must be true on every row produced, not just the
        // first.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 25); // more than the 15 bars needed for a single value
        await dbContext.SaveChangesAsync();

        var service = new AtrComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<AtrComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicators = await dbContext.AtrIndicators.ToListAsync();
        Assert.True(indicators.Count > 1, "expected more than one ATR row to exercise the 'every row' assertion");
        Assert.All(indicators, a => Assert.True(a.VerificationScopeLimited));
    }

    [Fact]
    public async Task ComputeAsync_FourteenBars_InsertsNothing()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 14); // one short of the 15 bars Wilder's seed requires

        var service = new AtrComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<AtrComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        Assert.Empty(dbContext.AtrIndicators);
    }

    [Fact]
    public async Task ComputeAsync_CalledTwiceForSameData_DoesNotInsertDuplicateRows()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 15);
        await dbContext.SaveChangesAsync();

        var service = new AtrComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<AtrComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);
        var countAfterFirstRun = await dbContext.AtrIndicators.CountAsync();

        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);
        var countAfterSecondRun = await dbContext.AtrIndicators.CountAsync();

        Assert.Equal(countAfterFirstRun, countAfterSecondRun);
    }

    [Fact]
    public async Task ComputeAsync_CalendarGapInsideWindow_SkipsTheResult()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // 15 bars exist, but day offset 3 has no bar at all - a real ingestion gap inside
        // ATR's 15-bar window.
        var barDayOffsets = Enumerable.Range(0, 3).Concat(Enumerable.Range(4, 13)).ToList(); // skips offset 3, 16 total
        for (var i = 0; i < barDayOffsets.Count; i++)
        {
            var offset = barDayOffsets[i];
            var timestamp = new DateTimeOffset(FirstDay.AddDays(offset).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 102m + i, 100m + i, 101m + i));
            dbContext.BarVerifications.Add(MakeVerification("AAPL", timestamp, verified: true));
        }
        await dbContext.SaveChangesAsync();

        // Calendar confirms day offset 3 was a real open trading day.
        var openDays = Enumerable.Range(0, 17).Select(o => FirstDay.AddDays(o)).ToHashSet();

        var service = new AtrComputationService(dbContext, new FakeTradingCalendarService(openDays), NullLogger<AtrComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        Assert.Empty(dbContext.AtrIndicators);
    }

    [Fact]
    public async Task ComputeAsync_ExDividendDateInsideWindow_SetsHasExDividendEventTrue()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 15); // window span: FirstDay through FirstDay+14
        dbContext.RawExDividendObservations.Add(new RawExDividendObservation
        {
            Source = SourceType.Finnhub,
            Symbol = "AAPL",
            ExDividendDate = FirstDay.AddDays(7), // inside the window's span
            Value = 0.24m,
            SampleCount = 1,
            Verified = false,
            IngestedAt = DateTimeOffset.UtcNow,
            RequestId = Guid.NewGuid()
        });
        await dbContext.SaveChangesAsync();

        var service = new AtrComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<AtrComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.AtrIndicators.SingleAsync();
        Assert.True(indicator.HasExDividendEvent);
        // Informational only - the value itself is untouched by the ex-div event.
        Assert.Equal(2m, indicator.Value);
    }

    [Fact]
    public async Task ComputeAsync_ExDividendDateOutsideWindow_SetsHasExDividendEventFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 25); // multiple results; window for bars[14] spans day0-day14
        dbContext.RawExDividendObservations.Add(new RawExDividendObservation
        {
            Source = SourceType.Finnhub,
            Symbol = "AAPL",
            ExDividendDate = FirstDay.AddDays(20), // outside the first result's window
            Value = 0.24m,
            SampleCount = 1,
            Verified = false,
            IngestedAt = DateTimeOffset.UtcNow,
            RequestId = Guid.NewGuid()
        });
        await dbContext.SaveChangesAsync();

        var service = new AtrComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<AtrComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var firstIndicator = await dbContext.AtrIndicators.SingleAsync(a => a.BarDate == FirstDay.AddDays(14));
        Assert.False(firstIndicator.HasExDividendEvent);
    }

    [Fact]
    public async Task ComputeAsync_NoBarsAtAll_InsertsNothing()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        var service = new AtrComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<AtrComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        Assert.Empty(dbContext.AtrIndicators);
    }

    [Fact]
    public async Task ComputeAsync_CorrectedBar_UsesTiingoPrimarySourceValueAndSetsProvenanceFlag()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // True Range depends on the PREVIOUS bar's Close, so a correction must land on a bar
        // that another bar's True Range actually references - the very last bar of a 15-bar
        // minimum window has no such follower, so day offset 13 (not 14) is corrected here:
        // its Alpaca Close (a bad print, 50) is resolved to Tiingo's Close (114), which
        // happens to restore the steady-drift fixture's true value, so the corrected result
        // reproduces the already-hand-verified 2m ATR seed exactly.
        for (var i = 0; i < 13; i++)
        {
            var timestamp = new DateTimeOffset(FirstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 102m + i, 100m + i, 101m + i));
            dbContext.BarVerifications.Add(MakeVerification("AAPL", timestamp, verified: true));
        }
        var day13 = new DateTimeOffset(FirstDay.AddDays(13).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        SeedTiingoCorrectedBar(dbContext, "AAPL", day13, high: 115m, low: 113m, alpacaClose: 50m, tiingoClose: 114m);
        var day14 = new DateTimeOffset(FirstDay.AddDays(14).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", day14, 116m, 114m, 115m));
        dbContext.BarVerifications.Add(MakeVerification("AAPL", day14, verified: true));
        await dbContext.SaveChangesAsync();

        var service = new AtrComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<AtrComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.AtrIndicators.SingleAsync();
        Assert.Equal(2m, indicator.Value); // matches the corrected (true) fixture value, not the 50 bad print
        Assert.True(indicator.HasTiingoCorrectedClose);
    }

    [Fact]
    public async Task ComputeAsync_OrdinaryUncorrectedBar_ProvenanceFlagFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 15); // no Discrepancy/correction rows at all
        await dbContext.SaveChangesAsync();

        var service = new AtrComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<AtrComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.AtrIndicators.SingleAsync();
        Assert.Equal(2m, indicator.Value);
        Assert.False(indicator.HasTiingoCorrectedClose);
    }

    [Fact]
    public async Task ComputeAsync_Jpm20260626RealValues_ComputesAtrFromTiingoCorrectedClose()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // Real values from this session's audit: JPM 2026-06-26 - Alpaca printed 327.50
        // (confirmed incorrect against real market data), Tiingo printed 329.05 (confirmed
        // correct), resolved via the OHL-agreed/Close-resolved-to-Tiingo exception. Placed at
        // day offset 13 (not 14) so the following day's True Range actually references it -
        // see the "CorrectedBar" test above for why the last bar alone wouldn't show an effect.
        for (var i = 0; i < 13; i++)
        {
            var timestamp = new DateTimeOffset(FirstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar("JPM", timestamp, 102m + i, 100m + i, 101m + i));
            dbContext.BarVerifications.Add(MakeVerification("JPM", timestamp, verified: true));
        }
        var jun26 = new DateTimeOffset(FirstDay.AddDays(13).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        SeedTiingoCorrectedBar(dbContext, "JPM", jun26, high: 115m, low: 113m, alpacaClose: 327.50m, tiingoClose: 329.05m);
        var jun27 = new DateTimeOffset(FirstDay.AddDays(14).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("JPM", jun27, 116m, 114m, 115m));
        dbContext.BarVerifications.Add(MakeVerification("JPM", jun27, verified: true));
        await dbContext.SaveChangesAsync();

        var service = new AtrComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<AtrComputationService>.Instance);
        await service.ComputeAsync("JPM", TickerSourceOrigin.Watchlist, CancellationToken.None);

        // Ground truth computed directly from the pure calculator, once with Tiingo's
        // corrected Close (expected) and once with Alpaca's raw bad print (to prove the two
        // genuinely differ, not a coincidence).
        var correctedBars = Enumerable.Range(0, 13)
            .Select(i => new AtrCalculator.AtrBarInput(FirstDay.AddDays(i), 102m + i, 100m + i, 101m + i))
            .Append(new AtrCalculator.AtrBarInput(FirstDay.AddDays(13), 115m, 113m, 329.05m))
            .Append(new AtrCalculator.AtrBarInput(FirstDay.AddDays(14), 116m, 114m, 115m))
            .ToList();
        var expectedCorrected = AtrCalculator.Compute(correctedBars).Single().Value;

        var uncorrectedBars = Enumerable.Range(0, 13)
            .Select(i => new AtrCalculator.AtrBarInput(FirstDay.AddDays(i), 102m + i, 100m + i, 101m + i))
            .Append(new AtrCalculator.AtrBarInput(FirstDay.AddDays(13), 115m, 113m, 327.50m))
            .Append(new AtrCalculator.AtrBarInput(FirstDay.AddDays(14), 116m, 114m, 115m))
            .ToList();
        var expectedIfUncorrected = AtrCalculator.Compute(uncorrectedBars).Single().Value;

        var indicator = await dbContext.AtrIndicators.SingleAsync();
        Assert.Equal(expectedCorrected, indicator.Value);
        Assert.NotEqual(expectedIfUncorrected, indicator.Value);
        Assert.True(indicator.HasTiingoCorrectedClose);
    }
}
