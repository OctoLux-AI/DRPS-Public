using Drps.Calculator.Dma;

namespace Drps.Tests.Calculator;

public class DmaGapCheckerTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // closes = 1, 2, 3, ... - same convention as DmaCalculatorTests, so expected DMA values
    // can be hand-computed from the arithmetic sequence.
    private static List<DmaCalculator.DmaBarInput> MakeBars(IEnumerable<int> dayOffsets) =>
        dayOffsets
            .Select(offset => new DmaCalculator.DmaBarInput(FirstDay.AddDays(offset), offset + 1m))
            .ToList();

    private static HashSet<DateOnly> DaysInRange(int startOffset, int endOffsetInclusive) =>
        Enumerable.Range(startOffset, endOffsetInclusive - startOffset + 1)
            .Select(FirstDay.AddDays)
            .ToHashSet();

    [Fact]
    public void Filter_NoGapsAtAll_AllWindowsComputeNormallyAndNothingIsSkipped()
    {
        var bars = MakeBars(Enumerable.Range(0, 70)); // 70 consecutive days, every day present
        var openTradingDays = DaysInRange(0, 69); // calendar matches the bars exactly

        var result = DmaGapChecker.Filter(bars, openTradingDays);

        Assert.Empty(result.SkippedWindows);
        var expected = DmaCalculator.Compute(bars);
        Assert.Equal(expected.Count, result.ClearResults.Count);
        Assert.Equal(expected.ToHashSet(), result.ClearResults.ToHashSet());
    }

    [Fact]
    public void Filter_GapOnlyReachableByDma60Window_SkipsOnlyDma60ForTheAffectedBar()
    {
        // 65 present bars spanning a 66-day calendar range with day offset 20 missing (a
        // genuine ingestion/verification gap - the market was open, no bar exists). Offsets
        // 0-19 are present as-is; offset 20 is skipped; offsets 21-65 shift the bar list by
        // one position relative to calendar day.
        var dayOffsets = Enumerable.Range(0, 20).Concat(Enumerable.Range(21, 45)); // 20 + 45 = 65 bars, missing offset 20
        var bars = MakeBars(dayOffsets);
        var openTradingDays = DaysInRange(0, 65); // calendar includes the missing day 20

        var result = DmaGapChecker.Filter(bars, openTradingDays);

        var lastDate = bars[^1].Date; // offset 65
        Assert.Equal(FirstDay.AddDays(65), lastDate);

        // DMA-60 for the last bar spans bar indices [5..64] -> dates [day5..day65], which
        // includes the missing day 20 - must be skipped.
        var skippedDma60 = Assert.Single(result.SkippedWindows, s => s.Window == 60 && s.WindowEndDate == lastDate);
        Assert.Contains(FirstDay.AddDays(20), skippedDma60.MissingDates);

        // DMA-5/15/30 for the SAME last bar start well after day 20 (index 60, 50, and 35
        // respectively - all map to days after 20) and must be unaffected.
        Assert.Contains(result.ClearResults, r => r.Date == lastDate && r.Window == 5);
        Assert.Contains(result.ClearResults, r => r.Date == lastDate && r.Window == 15);
        Assert.Contains(result.ClearResults, r => r.Date == lastDate && r.Window == 30);
        Assert.DoesNotContain(result.SkippedWindows, s => s.Window is 5 or 15 or 30 && s.WindowEndDate == lastDate);
    }

    [Fact]
    public void Filter_WeekendClosureBetweenBars_IsNotFlaggedAsAGap()
    {
        // Bars jump from a Friday (offset 4) straight to the following Monday (offset 7) -
        // offsets 5 and 6 (Saturday/Sunday) have no bars, same shape as a real gap on the
        // wire. The difference is the trading calendar: those two dates were never open
        // trading days in the first place, so they must never be checked at all.
        var dayOffsets = new[] { 0, 1, 2, 3, 4, 7, 8, 9, 10, 11 }; // 10 bars, weekend at 5-6 skipped
        var bars = MakeBars(dayOffsets);

        // Calendar explicitly excludes offsets 5 and 6 - they were closed, not missing.
        var openTradingDays = dayOffsets.Select(FirstDay.AddDays).ToHashSet();

        var result = DmaGapChecker.Filter(bars, openTradingDays);

        Assert.Empty(result.SkippedWindows);
        var expected = DmaCalculator.Compute(bars);
        Assert.Equal(expected.Count, result.ClearResults.Count);
        Assert.Equal(expected.ToHashSet(), result.ClearResults.ToHashSet());
    }
}
