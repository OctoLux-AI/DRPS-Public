using Drps.Calculator.Atr;

namespace Drps.Tests.Calculator;

public class AtrGapCheckerTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // Steady up-drift, constant 2-point range, keyed by calendar day offset (not list
    // index) so a skipped offset doesn't disturb the H/L/C pattern for the remaining bars -
    // gap-checking only cares about which dates get a result, not the ATR value itself.
    private static List<AtrCalculator.AtrBarInput> MakeBars(IEnumerable<int> dayOffsets) =>
        dayOffsets
            .Select(offset => new AtrCalculator.AtrBarInput(FirstDay.AddDays(offset), 102m + offset, 100m + offset, 101m + offset))
            .ToList();

    private static HashSet<DateOnly> DaysInRange(int startOffset, int endOffsetInclusive) =>
        Enumerable.Range(startOffset, endOffsetInclusive - startOffset + 1)
            .Select(FirstDay.AddDays)
            .ToHashSet();

    [Fact]
    public void Filter_NoGapsAtAll_AllResultsComputeNormallyAndNothingIsSkipped()
    {
        var bars = MakeBars(Enumerable.Range(0, 40)); // 40 consecutive days, every day present
        var openTradingDays = DaysInRange(0, 39);

        var result = AtrGapChecker.Filter(bars, openTradingDays);

        Assert.Empty(result.SkippedResults);
        var expected = AtrCalculator.Compute(bars);
        Assert.Equal(expected.Count, result.ClearResults.Count);
        Assert.Equal(expected.ToHashSet(), result.ClearResults.ToHashSet());
    }

    [Fact]
    public void Filter_GapInsideTrailingWindow_SkipsEarlyResultButClearsLaterResultOnceGapAgesOut()
    {
        // 45 present bars spanning a 46-day calendar range with day offset 5 missing (a
        // genuine gap). AtrGapChecker's trailing window is a fixed 15 bars
        // (AtrGapChecker.WindowSize) - this demonstrates the same "ages out over time"
        // behavior as RsiGapChecker: the SAME gap affects an early result (whose 15-bar
        // trailing window still reaches back to it) but not a later result.
        var dayOffsets = Enumerable.Range(0, 5).Concat(Enumerable.Range(6, 40)); // 5 + 40 = 45 bars, missing offset 5
        var bars = MakeBars(dayOffsets);
        var openTradingDays = DaysInRange(0, 45); // calendar includes the missing day 5

        var result = AtrGapChecker.Filter(bars, openTradingDays);

        var firstResultDate = bars[14].Date; // 15th present bar -> day 15 (index14 >= 5 shifts by 1)
        Assert.Equal(FirstDay.AddDays(15), firstResultDate);

        // First ATR result's window spans bars[0..14] -> dates [day0..day15], which
        // includes the missing day 5 - must be skipped. True Range's own previous-Close
        // dependency makes this especially important: without the gap check, the true
        // range calculated across the missing day would silently span two calendar days'
        // worth of price movement as if it were one.
        var skipped = Assert.Single(result.SkippedResults, s => s.Date == firstResultDate);
        Assert.Contains(FirstDay.AddDays(5), skipped.MissingDates);

        var lastResultDate = bars[^1].Date; // 45th present bar -> day 45
        Assert.Equal(FirstDay.AddDays(45), lastResultDate);

        // Last ATR result's window spans bars[24..44] -> dates [day25..day45], which does
        // NOT include day 5 - unaffected by the earlier gap.
        Assert.Contains(result.ClearResults, r => r.Date == lastResultDate);
        Assert.DoesNotContain(result.SkippedResults, s => s.Date == lastResultDate);
    }

    [Fact]
    public void Filter_WeekendClosureBetweenBars_IsNotFlaggedAsAGap()
    {
        // Bars jump from a Friday (offset 4) straight to the following Monday (offset 7) -
        // offsets 5 and 6 (Saturday/Sunday) have no bars. The trading calendar simply never
        // includes those two dates as open, so they must never be checked at all.
        var dayOffsets = new[] { 0, 1, 2, 3, 4 }.Concat(Enumerable.Range(7, 15)); // 5 + 15 = 20 bars, weekend at 5-6 skipped
        var bars = MakeBars(dayOffsets);

        var openTradingDays = dayOffsets.Select(FirstDay.AddDays).ToHashSet();

        var result = AtrGapChecker.Filter(bars, openTradingDays);

        Assert.Empty(result.SkippedResults);
        var expected = AtrCalculator.Compute(bars);
        Assert.Equal(expected.Count, result.ClearResults.Count);
        Assert.Equal(expected.ToHashSet(), result.ClearResults.ToHashSet());
    }
}
