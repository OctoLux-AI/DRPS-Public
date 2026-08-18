using Drps.Calculator.Rsi;

namespace Drps.Tests.Calculator;

public class RsiSlopeGapCheckerTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);
    private const int Lookback = 3;

    // Values are arbitrary (1, 2, 3, ...) - gap-checking only cares about which dates get a
    // result, not the underlying value, same convention as RsiGapCheckerTests.
    private static List<RsiSlopeCalculator.RsiSlopeInput> MakeSeries(IEnumerable<int> dayOffsets) =>
        dayOffsets
            .Select(offset => new RsiSlopeCalculator.RsiSlopeInput(FirstDay.AddDays(offset), offset + 1m))
            .ToList();

    private static HashSet<DateOnly> DaysInRange(int startOffset, int endOffsetInclusive) =>
        Enumerable.Range(startOffset, endOffsetInclusive - startOffset + 1)
            .Select(FirstDay.AddDays)
            .ToHashSet();

    [Fact]
    public void Filter_NoGapsAtAll_AllResultsComputeNormallyAndNothingIsSkipped()
    {
        var series = MakeSeries(Enumerable.Range(0, 20)); // 20 consecutive days
        var openTradingDays = DaysInRange(0, 19);

        var result = RsiSlopeGapChecker.Filter(series, Lookback, openTradingDays);

        Assert.Empty(result.SkippedResults);
        var expected = RsiSlopeCalculator.Compute(series, Lookback);
        Assert.Equal(expected.Count, result.ClearResults.Count);
        Assert.Equal(expected.ToHashSet(), result.ClearResults.ToHashSet());
    }

    [Fact]
    public void Filter_GapInsideTrailingWindow_SkipsAffectedResultsButClearsOnceGapAgesOut()
    {
        // 30 present RSI readings spanning a 31-day calendar range, missing day offset 5 - a
        // real gap in the underlying RSI series (RsiComputationService never wrote a row for
        // that date, e.g. because of its own upstream bar gap).
        var dayOffsets = Enumerable.Range(0, 5).Concat(Enumerable.Range(6, 25)); // missing offset 5
        var series = MakeSeries(dayOffsets);
        var openTradingDays = DaysInRange(0, 30); // calendar confirms day 5 was a real open trading day

        var result = RsiSlopeGapChecker.Filter(series, Lookback, openTradingDays);

        // The result at day 6 (the first present date after the gap) has a 4-wide window
        // ([day2, day3, day4, day6]) spanning across the missing day 5 - must be skipped.
        var affected = Assert.Single(result.SkippedResults, s => s.Date == FirstDay.AddDays(6));
        Assert.Contains(FirstDay.AddDays(5), affected.MissingDates);

        // The very last result (day 30 - Enumerable.Range(6, 25) runs 6..30) has a window
        // entirely past the gap - unaffected.
        var lastResultDate = series[^1].Date;
        Assert.Equal(FirstDay.AddDays(30), lastResultDate);
        Assert.Contains(result.ClearResults, r => r.Date == lastResultDate);
        Assert.DoesNotContain(result.SkippedResults, s => s.Date == lastResultDate);
    }

    [Fact]
    public void Filter_WeekendClosureBetweenReadings_IsNotFlaggedAsAGap()
    {
        // RSI readings jump from Friday (offset 4) straight to the following Monday (offset
        // 7) - offsets 5/6 simply never appear in the trading calendar at all, so they must
        // never be checked.
        var dayOffsets = new[] { 0, 1, 2, 3, 4 }.Concat(Enumerable.Range(7, 15));
        var series = MakeSeries(dayOffsets);
        var openTradingDays = dayOffsets.Select(FirstDay.AddDays).ToHashSet();

        var result = RsiSlopeGapChecker.Filter(series, Lookback, openTradingDays);

        Assert.Empty(result.SkippedResults);
        var expected = RsiSlopeCalculator.Compute(series, Lookback);
        Assert.Equal(expected.Count, result.ClearResults.Count);
        Assert.Equal(expected.ToHashSet(), result.ClearResults.ToHashSet());
    }
}
