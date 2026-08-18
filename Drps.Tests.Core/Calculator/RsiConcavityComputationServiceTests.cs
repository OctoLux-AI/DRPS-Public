using Drps.Calculator.Rsi;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Calculator;

public class RsiConcavityComputationServiceTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);
    private const int SlopeStartOffset = 3;

    // Same slope sequence hand-verified in RsiConcavityCalculatorTests
    // (ExpectedConcavity = -2, -5, -4, 3, 7, 0).
    private static readonly decimal[] SlopeValues = { 10m, 8m, 3m, -1m, 2m, 9m, 9m };

    private static RsiSlopeIndicator MakeSlopeRow(
        string symbol, DateOnly date, decimal value, int lookback = 3,
        bool hasExDividendEvent = false, bool hasTiingoCorrectedClose = false) => new()
    {
        Symbol = symbol,
        BarDate = date,
        Lookback = lookback,
        Value = value,
        ConfirmedDirection = SlopeConfirmationDirection.Unconfirmed, // irrelevant to concavity's own math
        HasExDividendEvent = hasExDividendEvent,
        HasTiingoCorrectedClose = hasTiingoCorrectedClose,
        VerificationScopeLimited = true,
        CalculationVersion = RsiSlopeComputationService.CalculationVersion,
        ComputedAt = DateTimeOffset.UtcNow
    };

    private static void SeedSlopeRows(
        Drps.Calculator.Persistence.CalculatorDbContext dbContext, string symbol, IReadOnlyList<decimal> values, int lookback = 3)
    {
        for (var i = 0; i < values.Count; i++)
        {
            dbContext.RsiSlopeIndicators.Add(MakeSlopeRow(symbol, FirstDay.AddDays(SlopeStartOffset + i), values[i], lookback));
        }
    }

    private static RsiConcavityComputationService MakeService(
        Drps.Calculator.Persistence.CalculatorDbContext dbContext,
        Drps.Calculator.Calendar.ITradingCalendarService? calendarService = null,
        int slopeLookback = 3) =>
        new(
            dbContext,
            calendarService ?? new FakeTradingCalendarService(),
            Options.Create(new Drps.Calculator.CalculatorSettings { RsiSlopeLookback = slopeLookback }),
            NullLogger<RsiConcavityComputationService>.Instance);

    [Fact]
    public async Task ComputeAsync_SevenSlopeRows_InsertsRowsMatchingHandCalculatedConcavitySequence()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedSlopeRows(dbContext, "AAPL", SlopeValues);
        await dbContext.SaveChangesAsync();

        var service = MakeService(dbContext);
        await service.ComputeAsync("AAPL", CancellationToken.None);

        var rows = await dbContext.RsiConcavityIndicators.OrderBy(r => r.BarDate).ToListAsync();

        var expectedValues = new[] { -2m, -5m, -4m, 3m, 7m, 0m };
        Assert.Equal(expectedValues.Length, rows.Count);
        for (var i = 0; i < expectedValues.Length; i++)
        {
            Assert.Equal(FirstDay.AddDays(SlopeStartOffset + i + 1), rows[i].BarDate);
            Assert.Equal(expectedValues[i], rows[i].Value);
            Assert.Equal(3, rows[i].SlopeLookback);
            Assert.Equal(RsiConcavityComputationService.CalculationVersion, rows[i].CalculationVersion);
            Assert.True(rows[i].VerificationScopeLimited);
        }
    }

    [Fact]
    public async Task ComputeAsync_HandCalculatedConcavitySequence_ConfirmedDirectionMatchesEvaluatorOutput()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedSlopeRows(dbContext, "AAPL", SlopeValues);
        await dbContext.SaveChangesAsync();

        var service = MakeService(dbContext);
        await service.ComputeAsync("AAPL", CancellationToken.None);

        var rows = await dbContext.RsiConcavityIndicators.OrderBy(r => r.BarDate).ToListAsync();

        // Same pattern hand-verified in RsiConcavityConfirmationEvaluatorTests - the streak-of-3
        // requirement means the 2 consecutive positive readings at the end (3, 7) do NOT confirm,
        // unlike RsiSlope's own streak-of-2 rule.
        var expectedDirections = new[]
        {
            SlopeConfirmationDirection.Unconfirmed,
            SlopeConfirmationDirection.Unconfirmed,
            SlopeConfirmationDirection.ConfirmedNegative,
            SlopeConfirmationDirection.Unconfirmed,
            SlopeConfirmationDirection.Unconfirmed,
            SlopeConfirmationDirection.Unconfirmed
        };
        Assert.Equal(expectedDirections, rows.Select(r => r.ConfirmedDirection).ToArray());
    }

    [Fact]
    public async Task ComputeAsync_NoSlopeRows_InsertsNothing()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        var service = MakeService(dbContext);
        await service.ComputeAsync("AAPL", CancellationToken.None);

        Assert.Empty(dbContext.RsiConcavityIndicators);
    }

    [Fact]
    public async Task ComputeAsync_CalledTwiceForSameData_DoesNotInsertDuplicateRows()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedSlopeRows(dbContext, "AAPL", SlopeValues);
        await dbContext.SaveChangesAsync();

        var service = MakeService(dbContext);
        await service.ComputeAsync("AAPL", CancellationToken.None);
        var countAfterFirstRun = await dbContext.RsiConcavityIndicators.CountAsync();

        await service.ComputeAsync("AAPL", CancellationToken.None);
        var countAfterSecondRun = await dbContext.RsiConcavityIndicators.CountAsync();

        Assert.Equal(countAfterFirstRun, countAfterSecondRun);
    }

    [Fact]
    public async Task ComputeAsync_ConfiguredLookbackDoesNotMatchSeededSlopeRows_InsertsNothing()
    {
        // Slope rows were seeded under Lookback = 4, but the service is configured to read
        // Lookback = 3 - only the currently-configured lookback's slope series is a live input,
        // same "only the latest version is live" convention as RsiSlopeComputationService's own
        // read of RsiIndicators.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedSlopeRows(dbContext, "AAPL", SlopeValues, lookback: 4);
        await dbContext.SaveChangesAsync();

        var service = MakeService(dbContext, slopeLookback: 3);
        await service.ComputeAsync("AAPL", CancellationToken.None);

        Assert.Empty(dbContext.RsiConcavityIndicators);
    }

    [Fact]
    public async Task ComputeAsync_CalendarGapBetweenSlopeReadings_SkipsTheAffectedResult()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // 6 present slope readings at relative offsets 0,1,3,4,5,6 (actual dates day3, day4,
        // day6, day7, day8, day9) - relative offset 2 (day5) has no slope row at all, a real
        // gap between two otherwise-adjacent slope readings (day4 and day6).
        var presentOffsets = new[] { 0, 1, 3, 4, 5, 6 };
        for (var i = 0; i < presentOffsets.Length; i++)
        {
            dbContext.RsiSlopeIndicators.Add(
                MakeSlopeRow("AAPL", FirstDay.AddDays(SlopeStartOffset + presentOffsets[i]), SlopeValues[i]));
        }
        await dbContext.SaveChangesAsync();

        var openDays = Enumerable.Range(SlopeStartOffset, 8).Select(FirstDay.AddDays).ToHashSet();

        var service = MakeService(dbContext, new FakeTradingCalendarService(openDays));
        await service.ComputeAsync("AAPL", CancellationToken.None);

        var rows = await dbContext.RsiConcavityIndicators.ToListAsync();

        // The reading at day6 (relative offset 3, the first present date after the gap) has a
        // 2-wide window spanning [day4, day6], which includes the missing day5 - must be
        // skipped.
        Assert.DoesNotContain(rows, r => r.BarDate == FirstDay.AddDays(SlopeStartOffset + 3));

        // The next reading, day7 (relative offset 4), has a window [day6, day7] fully past the
        // gap - unaffected.
        Assert.Contains(rows, r => r.BarDate == FirstDay.AddDays(SlopeStartOffset + 4));
    }

    [Fact]
    public async Task ComputeAsync_ExDividendFlagOnSlopeRowInsideWindow_PropagatesToConcavityRow()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        for (var i = 0; i < SlopeValues.Length; i++)
        {
            // The slope reading at relative offset 2 (day5) carries an ex-dividend flag.
            dbContext.RsiSlopeIndicators.Add(
                MakeSlopeRow("AAPL", FirstDay.AddDays(SlopeStartOffset + i), SlopeValues[i], hasExDividendEvent: i == 2));
        }
        await dbContext.SaveChangesAsync();

        var service = MakeService(dbContext);
        await service.ComputeAsync("AAPL", CancellationToken.None);

        var rows = await dbContext.RsiConcavityIndicators.OrderBy(r => r.BarDate).ToListAsync();

        // Concavity at day5 (window [day4,day5]) and day6 (window [day5,day6]) both include
        // the flagged day5 - both propagate. Concavity at day7 (window [day6,day7]) does not.
        Assert.True(rows.Single(r => r.BarDate == FirstDay.AddDays(SlopeStartOffset + 2)).HasExDividendEvent);
        Assert.True(rows.Single(r => r.BarDate == FirstDay.AddDays(SlopeStartOffset + 3)).HasExDividendEvent);
        Assert.False(rows.Single(r => r.BarDate == FirstDay.AddDays(SlopeStartOffset + 4)).HasExDividendEvent);
    }

    [Fact]
    public async Task ComputeAsync_TiingoCorrectedFlagOnSlopeRowInsideWindow_PropagatesToConcavityRow()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        for (var i = 0; i < SlopeValues.Length; i++)
        {
            dbContext.RsiSlopeIndicators.Add(
                MakeSlopeRow("AAPL", FirstDay.AddDays(SlopeStartOffset + i), SlopeValues[i], hasTiingoCorrectedClose: i == 2));
        }
        await dbContext.SaveChangesAsync();

        var service = MakeService(dbContext);
        await service.ComputeAsync("AAPL", CancellationToken.None);

        var rows = await dbContext.RsiConcavityIndicators.OrderBy(r => r.BarDate).ToListAsync();

        Assert.True(rows.Single(r => r.BarDate == FirstDay.AddDays(SlopeStartOffset + 2)).HasTiingoCorrectedClose);
        Assert.True(rows.Single(r => r.BarDate == FirstDay.AddDays(SlopeStartOffset + 3)).HasTiingoCorrectedClose);
        Assert.False(rows.Single(r => r.BarDate == FirstDay.AddDays(SlopeStartOffset + 4)).HasTiingoCorrectedClose);
    }
}
