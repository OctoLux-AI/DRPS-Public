using Drps.Calculator.Rsi;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Calculator;

public class RsiComputationServiceTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // Same hand-verified close sequence as RsiCalculatorTests (15 closes -> exactly one
    // seed RSI value of 75, hand-calculated there).
    private static readonly decimal[] Closes =
    {
        100m, 102m, 101m, 103m, 104m, 102m, 105m, 107m, 106m, 108m, 110m, 109m, 111m, 113m, 112m
    };

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
        SourceCount = verified ? 2 : 1,
        MatchedSourceCount = verified ? 2 : 1,
        Verified = verified,
        ToleranceApplied = 0.001m,
        ComputationVersion = 1,
        EvaluatedAt = DateTimeOffset.UtcNow
    };

    private static void SeedBars(Drps.Calculator.Persistence.CalculatorDbContext dbContext, string symbol, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var timestamp = new DateTimeOffset(FirstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar(symbol, timestamp, Closes[i]));
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
    public async Task ComputeAsync_FifteenBars_InsertsOneRsiRowMatchingHandCalculatedValue()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 15);
        await dbContext.SaveChangesAsync();

        var service = new RsiComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RsiComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.RsiIndicators.SingleAsync();
        Assert.Equal(14, indicator.Period);
        Assert.Equal(75m, indicator.Value); // hand-calculated in RsiCalculatorTests
        Assert.Equal(RsiComputationService.CalculationVersion, indicator.CalculationVersion);
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
        // proves ComputeAsync faithfully stamps whatever it's given onto every new RSI row.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        for (var i = 0; i < 25; i++)
        {
            var timestamp = new DateTimeOffset(FirstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 100m + i));
            dbContext.BarVerifications.Add(MakeVerification("AAPL", timestamp, verified: true));
        }
        await dbContext.SaveChangesAsync();

        var service = new RsiComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RsiComputationService>.Instance);
        await service.ComputeAsync("AAPL", sourceOrigin, CancellationToken.None);

        var indicators = await dbContext.RsiIndicators.ToListAsync();
        Assert.NotEmpty(indicators);
        Assert.All(indicators, i => Assert.Equal(sourceOrigin, i.TickerSourceOrigin));
    }

    [Fact]
    public async Task ComputeAsync_MultipleRsiRowsProduced_VerificationScopeLimitedIsTrueOnEveryRow()
    {
        // VerificationScopeLimited is a permanent disclaimer about this indicator type, not
        // a per-row computed condition - it must be true on every row produced, not just the
        // first. Uses a longer bar sequence (constant closes are fine here; only the flag
        // matters) so multiple RSI rows actually get persisted.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        for (var i = 0; i < 25; i++)
        {
            var timestamp = new DateTimeOffset(FirstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 100m + i));
            dbContext.BarVerifications.Add(MakeVerification("AAPL", timestamp, verified: true));
        }
        await dbContext.SaveChangesAsync();

        var service = new RsiComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RsiComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicators = await dbContext.RsiIndicators.ToListAsync();
        Assert.True(indicators.Count > 1, "expected more than one RSI row to exercise the 'every row' assertion");
        Assert.All(indicators, i => Assert.True(i.VerificationScopeLimited));
    }

    [Fact]
    public async Task ComputeAsync_FourteenBars_InsertsNothing()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 14); // one short of the 15 bars Wilder's seed requires

        var service = new RsiComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RsiComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        Assert.Empty(dbContext.RsiIndicators);
    }

    [Fact]
    public async Task ComputeAsync_CalledTwiceForSameData_DoesNotInsertDuplicateRows()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 15);
        await dbContext.SaveChangesAsync();

        var service = new RsiComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RsiComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);
        var countAfterFirstRun = await dbContext.RsiIndicators.CountAsync();

        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);
        var countAfterSecondRun = await dbContext.RsiIndicators.CountAsync();

        Assert.Equal(countAfterFirstRun, countAfterSecondRun);
    }

    [Fact]
    public async Task ComputeAsync_CalendarGapInsideWindow_SkipsTheResult()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // 15 bars exist, but day offset 3 has no bar at all - a real ingestion gap inside
        // RSI's 15-bar window.
        var barDayOffsets = Enumerable.Range(0, 3).Concat(Enumerable.Range(4, 13)).ToList(); // skips offset 3, 16 total
        for (var i = 0; i < barDayOffsets.Count; i++)
        {
            var offset = barDayOffsets[i];
            var timestamp = new DateTimeOffset(FirstDay.AddDays(offset).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, Closes[i % Closes.Length]));
            dbContext.BarVerifications.Add(MakeVerification("AAPL", timestamp, verified: true));
        }
        await dbContext.SaveChangesAsync();

        // Calendar confirms day offset 3 was a real open trading day.
        var openDays = Enumerable.Range(0, 17).Select(o => FirstDay.AddDays(o)).ToHashSet();

        var service = new RsiComputationService(dbContext, new FakeTradingCalendarService(openDays), NullLogger<RsiComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        Assert.Empty(dbContext.RsiIndicators);
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

        var service = new RsiComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RsiComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.RsiIndicators.SingleAsync();
        Assert.True(indicator.HasExDividendEvent);
        // Informational only - the value itself is untouched by the ex-div event.
        Assert.Equal(75m, indicator.Value);
    }

    [Fact]
    public async Task ComputeAsync_NoBarsAtAll_InsertsNothing()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        var service = new RsiComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RsiComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        Assert.Empty(dbContext.RsiIndicators);
    }

    [Fact]
    public async Task ComputeAsync_CorrectedBar_UsesTiingoPrimarySourceValueAndSetsProvenanceFlag()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // First 14 bars unchanged from the hand-verified fixture; the 15th (last) day's
        // Alpaca Close is a bad print (500) resolved to Tiingo's corrected Close (112) -
        // which happens to restore the fixture's original true value, so the corrected
        // result should reproduce the already-hand-verified 75m seed RSI exactly.
        for (var i = 0; i < 14; i++)
        {
            var timestamp = new DateTimeOffset(FirstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, Closes[i]));
            dbContext.BarVerifications.Add(MakeVerification("AAPL", timestamp, verified: true));
        }
        var lastDay = new DateTimeOffset(FirstDay.AddDays(14).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        SeedTiingoCorrectedBar(dbContext, "AAPL", lastDay, alpacaClose: 500m, tiingoClose: Closes[14]);
        await dbContext.SaveChangesAsync();

        var service = new RsiComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RsiComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.RsiIndicators.SingleAsync();
        Assert.Equal(75m, indicator.Value); // matches the corrected (true) fixture value, not the 500 bad print
        Assert.True(indicator.HasTiingoCorrectedClose);
    }

    [Fact]
    public async Task ComputeAsync_OrdinaryUncorrectedBar_ProvenanceFlagFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 15); // no Discrepancy/correction rows at all
        await dbContext.SaveChangesAsync();

        var service = new RsiComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RsiComputationService>.Instance);
        await service.ComputeAsync("AAPL", TickerSourceOrigin.Watchlist, CancellationToken.None);

        var indicator = await dbContext.RsiIndicators.SingleAsync();
        Assert.Equal(75m, indicator.Value);
        Assert.False(indicator.HasTiingoCorrectedClose);
    }

    [Fact]
    public async Task ComputeAsync_Jpm20260626RealValues_ComputesRsiFromTiingoCorrectedClose()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // Real values from this session's audit: JPM 2026-06-26 - Alpaca printed 327.50
        // (confirmed incorrect against real market data), Tiingo printed 329.05 (confirmed
        // correct), resolved via the OHL-agreed/Close-resolved-to-Tiingo exception. Reuses
        // the existing 15-Closes fixture for the first 14 days; day 15 (index 14) is JPM's
        // real corrected date.
        for (var i = 0; i < 14; i++)
        {
            var timestamp = new DateTimeOffset(FirstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar("JPM", timestamp, Closes[i]));
            dbContext.BarVerifications.Add(MakeVerification("JPM", timestamp, verified: true));
        }
        var jun26 = new DateTimeOffset(FirstDay.AddDays(14).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        SeedTiingoCorrectedBar(dbContext, "JPM", jun26, alpacaClose: 327.50m, tiingoClose: 329.05m);
        await dbContext.SaveChangesAsync();

        var service = new RsiComputationService(dbContext, new FakeTradingCalendarService(), NullLogger<RsiComputationService>.Instance);
        await service.ComputeAsync("JPM", TickerSourceOrigin.Watchlist, CancellationToken.None);

        // Ground truth computed directly from the pure calculator, once with Tiingo's
        // corrected Close (expected) and once with Alpaca's raw bad print (to prove the two
        // genuinely differ, not a coincidence).
        var correctedBars = Closes.Take(14)
            .Select((c, i) => new RsiCalculator.RsiBarInput(FirstDay.AddDays(i), c))
            .Append(new RsiCalculator.RsiBarInput(FirstDay.AddDays(14), 329.05m))
            .ToList();
        var expectedCorrected = RsiCalculator.Compute(correctedBars).Single().Value;

        var uncorrectedBars = Closes.Take(14)
            .Select((c, i) => new RsiCalculator.RsiBarInput(FirstDay.AddDays(i), c))
            .Append(new RsiCalculator.RsiBarInput(FirstDay.AddDays(14), 327.50m))
            .ToList();
        var expectedIfUncorrected = RsiCalculator.Compute(uncorrectedBars).Single().Value;

        var indicator = await dbContext.RsiIndicators.SingleAsync();
        Assert.Equal(expectedCorrected, indicator.Value);
        Assert.NotEqual(expectedIfUncorrected, indicator.Value);
        Assert.True(indicator.HasTiingoCorrectedClose);
    }
}
