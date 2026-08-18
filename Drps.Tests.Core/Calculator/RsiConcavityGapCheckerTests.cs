using Drps.Calculator.Rsi;

namespace Drps.Tests.Calculator;

public class RsiConcavityGapCheckerTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    private static List<RsiConcavityCalculator.RsiConcavityInput> MakeSeries(IEnumerable<int> dayOffsets) =>
        dayOffsets
            .Select(offset => new RsiConcavityCalculator.RsiConcavityInput(FirstDay.AddDays(offset), offset + 1m))
            .ToList();

    private static HashSet<DateOnly> DaysInRange(int startOffset, int endOffsetInclusive) =>
        Enumerable.Range(startOffset, endOffsetInclusive - startOffset + 1)
            .Select(FirstDay.AddDays)
            .ToHashSet();

    [Fact]
    public void Filter_NoGapsAtAll_AllResultsComputeNormallyAndNothingIsSkipped()
    {
        var series = MakeSeries(Enumerable.Range(0, 10));
        var openTradingDays = DaysInRange(0, 9);

        var result = RsiConcavityGapChecker.Filter(series, openTradingDays);

        Assert.Empty(result.SkippedResults);
        var expected = RsiConcavityCalculator.Compute(series);
        Assert.Equal(expected.Count, result.ClearResults.Count);
        Assert.Equal(expected.ToHashSet(), result.ClearResults.ToHashSet());
    }

    [Fact]
    public void Filter_GapBetweenConsecutiveSlopeReadings_SkipsOnlyTheAdjacentResultAndClearsImmediatelyAfter()
    {
        // 15 present slope readings, missing day offset 5 - a real gap between two otherwise-
        // adjacent slope readings (day4 and day6).
        var dayOffsets = Enumerable.Range(0, 5).Concat(Enumerable.Range(6, 10)); // missing offset 5
        var series = MakeSeries(dayOffsets);
        var openTradingDays = DaysInRange(0, 15);

        var result = RsiConcavityGapChecker.Filter(series, openTradingDays);

        // Concavity at day6 depends on the immediately preceding slope reading (day4) - its
        // fixed 2-wide window [day4, day6] spans across the missing day5.
        var affected = Assert.Single(result.SkippedResults, s => s.Date == FirstDay.AddDays(6));
        Assert.Contains(FirstDay.AddDays(5), affected.MissingDates);

        // Concavity at day7 depends only on day6/day7 - the gap has already aged out of this
        // fixed 2-wide window (unlike RsiSlope's wider, lookback-dependent window, concavity's
        // window is always exactly 2, so it clears on the very next reading).
        Assert.Contains(result.ClearResults, r => r.Date == FirstDay.AddDays(7));
        Assert.DoesNotContain(result.SkippedResults, s => s.Date == FirstDay.AddDays(7));
    }

    [Fact]
    public void Filter_WeekendClosureBetweenReadings_IsNotFlaggedAsAGap()
    {
        var dayOffsets = new[] { 0, 1, 2, 3, 4 }.Concat(Enumerable.Range(7, 8));
        var series = MakeSeries(dayOffsets);
        var openTradingDays = dayOffsets.Select(FirstDay.AddDays).ToHashSet();

        var result = RsiConcavityGapChecker.Filter(series, openTradingDays);

        Assert.Empty(result.SkippedResults);
        var expected = RsiConcavityCalculator.Compute(series);
        Assert.Equal(expected.Count, result.ClearResults.Count);
        Assert.Equal(expected.ToHashSet(), result.ClearResults.ToHashSet());
    }
}
