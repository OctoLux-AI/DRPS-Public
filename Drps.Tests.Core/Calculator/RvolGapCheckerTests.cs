using Drps.Calculator.Rvol;

namespace Drps.Tests.Calculator;

public class RvolGapCheckerTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // Constant volume of 1000 for every bar - gap-checking only cares about which dates get
    // a result, not the RVOL value itself.
    private static List<RvolCalculator.RvolBarInput> MakeBars(IEnumerable<int> dayOffsets) =>
        dayOffsets
            .Select(offset => new RvolCalculator.RvolBarInput(FirstDay.AddDays(offset), 1000L))
            .ToList();

    private static HashSet<DateOnly> DaysInRange(int startOffset, int endOffsetInclusive) =>
        Enumerable.Range(startOffset, endOffsetInclusive - startOffset + 1)
            .Select(FirstDay.AddDays)
            .ToHashSet();

    [Fact]
    public void Filter_NoGapsAtAll_AllResultsComputeNormallyAndNothingIsSkipped()
    {
        var bars = MakeBars(Enumerable.Range(0, 50)); // 50 consecutive days, every day present
        var openTradingDays = DaysInRange(0, 49);

        var result = RvolGapChecker.Filter(bars, openTradingDays);

        Assert.Empty(result.SkippedResults);
        var expected = RvolCalculator.Compute(bars);
        Assert.Equal(expected.Count, result.ClearResults.Count);
        Assert.Equal(expected.ToHashSet(), result.ClearResults.ToHashSet());
    }

    [Fact]
    public void Filter_GapInsideTrailingWindow_SkipsEarlyResultButClearsLaterResultOnceGapAgesOut()
    {
        // 45 present bars spanning a 46-day calendar range with day offset 5 missing (a
        // genuine gap). RvolGapChecker's trailing window is a fixed 21 bars
        // (RvolCalculator.WindowSize) - this demonstrates the same "ages out over time"
        // behavior as RsiGapChecker: the SAME gap affects an early result (whose 21-bar
        // trailing window still reaches back to it) but not a later result (once the gap
        // has aged out of that fixed window).
        var dayOffsets = Enumerable.Range(0, 5).Concat(Enumerable.Range(6, 40)); // 5 + 40 = 45 bars, missing offset 5
        var bars = MakeBars(dayOffsets);
        var openTradingDays = DaysInRange(0, 45); // calendar includes the missing day 5

        var result = RvolGapChecker.Filter(bars, openTradingDays);

        var firstResultDate = bars[20].Date; // 21st present bar -> day 21 (index20 >= 5 shifts by 1)
        Assert.Equal(FirstDay.AddDays(21), firstResultDate);

        // First RVOL result's window spans bars[0..20] -> dates [day0..day21], which
        // includes the missing day 5 - must be skipped.
        var skipped = Assert.Single(result.SkippedResults, s => s.Date == firstResultDate);
        Assert.Contains(FirstDay.AddDays(5), skipped.MissingDates);

        var lastResultDate = bars[^1].Date; // 45th present bar -> day 45
        Assert.Equal(FirstDay.AddDays(45), lastResultDate);

        // Last RVOL result's window spans bars[24..44] -> dates [day25..day45], which does
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
        var dayOffsets = new[] { 0, 1, 2, 3, 4 }.Concat(Enumerable.Range(7, 21)); // 5 + 21 = 26 bars, weekend at 5-6 skipped
        var bars = MakeBars(dayOffsets);

        var openTradingDays = dayOffsets.Select(FirstDay.AddDays).ToHashSet();

        var result = RvolGapChecker.Filter(bars, openTradingDays);

        Assert.Empty(result.SkippedResults);
        var expected = RvolCalculator.Compute(bars);
        Assert.Equal(expected.Count, result.ClearResults.Count);
        Assert.Equal(expected.ToHashSet(), result.ClearResults.ToHashSet());
    }
}
