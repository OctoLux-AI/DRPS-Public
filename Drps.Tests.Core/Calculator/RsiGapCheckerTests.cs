using Drps.Calculator.Rsi;

namespace Drps.Tests.Calculator;

public class RsiGapCheckerTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // closes = 1, 2, 3, ... - same convention as RsiCalculatorTests uses hand-verified
    // values elsewhere; gap-checking only cares about which dates get a result, not the
    // RSI value itself, so a simple arithmetic sequence keeps these tests focused.
    private static List<RsiCalculator.RsiBarInput> MakeBars(IEnumerable<int> dayOffsets) =>
        dayOffsets
            .Select(offset => new RsiCalculator.RsiBarInput(FirstDay.AddDays(offset), offset + 1m))
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

        var result = RsiGapChecker.Filter(bars, openTradingDays);

        Assert.Empty(result.SkippedResults);
        var expected = RsiCalculator.Compute(bars);
        Assert.Equal(expected.Count, result.ClearResults.Count);
        Assert.Equal(expected.ToHashSet(), result.ClearResults.ToHashSet());
    }

    [Fact]
    public void Filter_GapInsideTrailingWindow_SkipsEarlyResultButClearsLaterResultOnceGapAgesOut()
    {
        // 30 present bars spanning a 31-day calendar range with day offset 5 missing (a
        // genuine gap - the market was open, no bar exists). RsiGapChecker's trailing window
        // is a fixed 15 bars (RsiGapChecker.WindowSize), unlike DMA's four different window
        // sizes - so this test demonstrates the RSI-specific behavior instead: the SAME gap
        // affects an early result (whose 15-bar trailing window still reaches back to it)
        // but not a later result (once the gap has aged out of that fixed window).
        var dayOffsets = Enumerable.Range(0, 5).Concat(Enumerable.Range(6, 25)); // 5 + 25 = 30 bars, missing offset 5
        var bars = MakeBars(dayOffsets);
        var openTradingDays = DaysInRange(0, 30); // calendar includes the missing day 5

        var result = RsiGapChecker.Filter(bars, openTradingDays);

        var firstResultDate = bars[14].Date; // 15th present bar -> day 15 (index14 >= 5 shifts by 1)
        Assert.Equal(FirstDay.AddDays(15), firstResultDate);

        // First RSI result's window spans bars[0..14] -> dates [day0..day15], which
        // includes the missing day 5 - must be skipped.
        var skipped = Assert.Single(result.SkippedResults, s => s.Date == firstResultDate);
        Assert.Contains(FirstDay.AddDays(5), skipped.MissingDates);

        var lastResultDate = bars[^1].Date; // 30th present bar -> day 30
        Assert.Equal(FirstDay.AddDays(30), lastResultDate);

        // Last RSI result's window spans bars[15..29] -> dates [day16..day30], which does
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

        var result = RsiGapChecker.Filter(bars, openTradingDays);

        Assert.Empty(result.SkippedResults);
        var expected = RsiCalculator.Compute(bars);
        Assert.Equal(expected.Count, result.ClearResults.Count);
        Assert.Equal(expected.ToHashSet(), result.ClearResults.ToHashSet());
    }
}
