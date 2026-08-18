using Drps.Calculator.Rsi;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Calculator;

public class RsiSlopeComputationServiceTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // Same 10-value hand-verified RSI sequence as RsiSlopeCalculatorTests
    // (ExpectedSlopeLookback3 = 10, 8, 3, -1, 2, 9, 9 for lookback 3).
    private static readonly decimal[] RsiValues = { 50m, 55m, 58m, 60m, 63m, 61m, 59m, 65m, 70m, 68m };

    private static RsiIndicator MakeRsiRow(
        string symbol, DateOnly date, decimal value, bool hasExDividendEvent = false, bool hasTiingoCorrectedClose = false) => new()
    {
        Symbol = symbol,
        BarDate = date,
        Period = RsiCalculator.Period,
        Value = value,
        HasExDividendEvent = hasExDividendEvent,
        HasTiingoCorrectedClose = hasTiingoCorrectedClose,
        VerificationScopeLimited = true,
        CalculationVersion = RsiComputationService.CalculationVersion,
        ComputedAt = DateTimeOffset.UtcNow
    };

    private static void SeedRsiRows(
        Drps.Calculator.Persistence.CalculatorDbContext dbContext, string symbol, IReadOnlyList<decimal> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            dbContext.RsiIndicators.Add(MakeRsiRow(symbol, FirstDay.AddDays(i), values[i]));
        }
    }

    private static RsiSlopeComputationService MakeService(
        Drps.Calculator.Persistence.CalculatorDbContext dbContext,
        Drps.Calculator.Calendar.ITradingCalendarService? calendarService = null,
        int lookback = 3) =>
        new(
            dbContext,
            calendarService ?? new FakeTradingCalendarService(),
            Options.Create(new Drps.Calculator.CalculatorSettings { RsiSlopeLookback = lookback }),
            NullLogger<RsiSlopeComputationService>.Instance);

    [Fact]
    public async Task ComputeAsync_TenRsiRows_InsertsRowsMatchingHandCalculatedSlopeSequence()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedRsiRows(dbContext, "AAPL", RsiValues);
        await dbContext.SaveChangesAsync();

        var service = MakeService(dbContext);
        await service.ComputeAsync("AAPL", CancellationToken.None);

        var rows = await dbContext.RsiSlopeIndicators.OrderBy(r => r.BarDate).ToListAsync();

        var expectedValues = new[] { 10m, 8m, 3m, -1m, 2m, 9m, 9m };
        Assert.Equal(expectedValues.Length, rows.Count);
        for (var i = 0; i < expectedValues.Length; i++)
        {
            Assert.Equal(FirstDay.AddDays(i + 3), rows[i].BarDate);
            Assert.Equal(expectedValues[i], rows[i].Value);
            Assert.Equal(3, rows[i].Lookback);
            Assert.Equal(RsiSlopeComputationService.CalculationVersion, rows[i].CalculationVersion);
            Assert.True(rows[i].VerificationScopeLimited);
        }
    }

    [Fact]
    public async Task ComputeAsync_HandCalculatedSlopeSequence_ConfirmedDirectionMatchesEvaluatorOutput()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedRsiRows(dbContext, "AAPL", RsiValues);
        await dbContext.SaveChangesAsync();

        var service = MakeService(dbContext);
        await service.ComputeAsync("AAPL", CancellationToken.None);

        var rows = await dbContext.RsiSlopeIndicators.OrderBy(r => r.BarDate).ToListAsync();

        // Same pattern hand-verified in RsiSlopeConfirmationEvaluatorTests for this exact
        // sequence - proves the computation service actually applies the confirmation filter,
        // not just persists the raw value.
        var expectedDirections = new[]
        {
            SlopeConfirmationDirection.Unconfirmed,
            SlopeConfirmationDirection.ConfirmedPositive,
            SlopeConfirmationDirection.ConfirmedPositive,
            SlopeConfirmationDirection.Unconfirmed,
            SlopeConfirmationDirection.Unconfirmed,
            SlopeConfirmationDirection.ConfirmedPositive,
            SlopeConfirmationDirection.ConfirmedPositive
        };
        Assert.Equal(expectedDirections, rows.Select(r => r.ConfirmedDirection).ToArray());
    }

    [Fact]
    public async Task ComputeAsync_NoRsiRows_InsertsNothing()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        var service = MakeService(dbContext);
        await service.ComputeAsync("AAPL", CancellationToken.None);

        Assert.Empty(dbContext.RsiSlopeIndicators);
    }

    [Fact]
    public async Task ComputeAsync_CalledTwiceForSameData_DoesNotInsertDuplicateRows()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedRsiRows(dbContext, "AAPL", RsiValues);
        await dbContext.SaveChangesAsync();

        var service = MakeService(dbContext);
        await service.ComputeAsync("AAPL", CancellationToken.None);
        var countAfterFirstRun = await dbContext.RsiSlopeIndicators.CountAsync();

        await service.ComputeAsync("AAPL", CancellationToken.None);
        var countAfterSecondRun = await dbContext.RsiSlopeIndicators.CountAsync();

        Assert.Equal(countAfterFirstRun, countAfterSecondRun);
    }

    [Fact]
    public async Task ComputeAsync_DifferentConfiguredLookback_UsesConfiguredValueNotADefault()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedRsiRows(dbContext, "AAPL", RsiValues);
        await dbContext.SaveChangesAsync();

        var service = MakeService(dbContext, lookback: 4);
        await service.ComputeAsync("AAPL", CancellationToken.None);

        var rows = await dbContext.RsiSlopeIndicators.OrderBy(r => r.BarDate).ToListAsync();

        // Lookback 4: RsiValues[i] - RsiValues[i-4] for i = 4..9.
        // 63-50=13, 61-55=6, 59-58=1, 65-60=5, 70-63=7, 68-61=7
        var expectedValues = new[] { 13m, 6m, 1m, 5m, 7m, 7m };
        Assert.Equal(expectedValues.Length, rows.Count);
        Assert.All(rows, r => Assert.Equal(4, r.Lookback));
        for (var i = 0; i < expectedValues.Length; i++)
        {
            Assert.Equal(expectedValues[i], rows[i].Value);
        }
    }

    [Fact]
    public async Task ComputeAsync_CalendarGapInsideWindow_SkipsTheAffectedResult()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // 10 RSI rows exist, but day offset 2 has no RSI row at all - a real gap in the
        // underlying RSI series.
        var dayOffsets = new[] { 0, 1, 3, 4, 5, 6, 7, 8, 9 }; // skips offset 2
        for (var i = 0; i < dayOffsets.Length; i++)
        {
            dbContext.RsiIndicators.Add(MakeRsiRow("AAPL", FirstDay.AddDays(dayOffsets[i]), RsiValues[i]));
        }
        await dbContext.SaveChangesAsync();

        // Calendar confirms day offset 2 was a real open trading day.
        var openDays = Enumerable.Range(0, 10).Select(o => FirstDay.AddDays(o)).ToHashSet();

        var service = MakeService(dbContext, new FakeTradingCalendarService(openDays));
        await service.ComputeAsync("AAPL", CancellationToken.None);

        // day4 (present index 3, the first index a lookback-3 result could exist at) has a
        // 4-wide window [day0, day1, day3, day4] spanning across the missing day2 - skipped.
        var rows = await dbContext.RsiSlopeIndicators.ToListAsync();
        Assert.DoesNotContain(rows, r => r.BarDate == FirstDay.AddDays(4));

        // day9's window ([day6..day9], past the gap) is unaffected - confirms the check is
        // scoped to the gap's actual reach, not a blanket "nothing computes after a gap ever."
        Assert.Contains(rows, r => r.BarDate == FirstDay.AddDays(9));
    }

    [Fact]
    public async Task ComputeAsync_ExDividendFlagOnRsiRowInsideWindow_PropagatesToSlopeRow()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        for (var i = 0; i < RsiValues.Length; i++)
        {
            // Day offset 1 (index 1) is flagged as an ex-dividend event on the underlying RSI
            // row.
            dbContext.RsiIndicators.Add(MakeRsiRow("AAPL", FirstDay.AddDays(i), RsiValues[i], hasExDividendEvent: i == 1));
        }
        await dbContext.SaveChangesAsync();

        var service = MakeService(dbContext);
        await service.ComputeAsync("AAPL", CancellationToken.None);

        var rows = await dbContext.RsiSlopeIndicators.OrderBy(r => r.BarDate).ToListAsync();

        // Lookback 3: day3's window is [day0..day3] (includes day1) -> flagged.
        // day4's window is [day1..day4] (includes day1) -> flagged.
        // day5's window is [day2..day5] (does NOT include day1) -> not flagged.
        Assert.True(rows.Single(r => r.BarDate == FirstDay.AddDays(3)).HasExDividendEvent);
        Assert.True(rows.Single(r => r.BarDate == FirstDay.AddDays(4)).HasExDividendEvent);
        Assert.False(rows.Single(r => r.BarDate == FirstDay.AddDays(5)).HasExDividendEvent);
    }

    [Fact]
    public async Task ComputeAsync_TiingoCorrectedFlagOnRsiRowInsideWindow_PropagatesToSlopeRow()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        for (var i = 0; i < RsiValues.Length; i++)
        {
            // Day offset 7 (index 7) was itself computed from a Tiingo-corrected Close.
            dbContext.RsiIndicators.Add(MakeRsiRow("AAPL", FirstDay.AddDays(i), RsiValues[i], hasTiingoCorrectedClose: i == 7));
        }
        await dbContext.SaveChangesAsync();

        var service = MakeService(dbContext);
        await service.ComputeAsync("AAPL", CancellationToken.None);

        var rows = await dbContext.RsiSlopeIndicators.OrderBy(r => r.BarDate).ToListAsync();

        // day7's own window [day4..day7] includes day7 itself -> flagged.
        Assert.True(rows.Single(r => r.BarDate == FirstDay.AddDays(7)).HasTiingoCorrectedClose);
        // day9's window [day6..day9] also includes day7 -> flagged.
        Assert.True(rows.Single(r => r.BarDate == FirstDay.AddDays(9)).HasTiingoCorrectedClose);
        // day3's window [day0..day3] does not include day7 -> not flagged.
        Assert.False(rows.Single(r => r.BarDate == FirstDay.AddDays(3)).HasTiingoCorrectedClose);
    }
}
