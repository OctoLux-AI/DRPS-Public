using Drps.Calculator.Atr;

namespace Drps.Tests.Calculator;

public class AtrCalculatorTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // Hand-calculated reference sequence (not a published dataset - constructed and
    // verified by hand here so the expected values are independently auditable).
    // Days 0-14 (15 bars): High = 102+i, Low = 100+i, Close = 101+i - a steady 1-point
    // up-drift each day with a constant 2-point daily range and no gaps, so for i=1..14:
    //   True Range[i] = max(High[i]-Low[i]=2, |High[i]-Close[i-1]|=|(102+i)-(100+i)|=2,
    //                        |Low[i]-Close[i-1]|=|(100+i)-(100+i)|=0) = 2
    //   ATR seed (at day14) = sum(TR[1..14])/14 = 14*2/14 = 2 exactly.
    // Day 15 (16th bar) deliberately gaps far above the prior close (High=125, Low=123,
    // Close=124) to exercise the max()/gap term against a real previous close of 115:
    //   True Range[15] = max(125-123=2, |125-115|=10, |123-115|=8) = 10
    //   ATR' = (ATR*13 + 10)/14 = (2*13+10)/14 = 36/14 = 18/7 ~= 2.571428571428571...
    private static List<AtrCalculator.AtrBarInput> MakeSeedBars(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new AtrCalculator.AtrBarInput(FirstDay.AddDays(i), 102m + i, 100m + i, 101m + i))
            .ToList();

    private static List<AtrCalculator.AtrBarInput> MakeSixteenBarsWithGapOnLastDay()
    {
        var bars = MakeSeedBars(15);
        bars.Add(new AtrCalculator.AtrBarInput(FirstDay.AddDays(15), 125m, 123m, 124m));
        return bars;
    }

    [Fact]
    public void Compute_FewerThanFifteenBars_ReturnsNoResults()
    {
        var bars = MakeSeedBars(14); // Period + 1 = 15 required for the first value

        var results = AtrCalculator.Compute(bars);

        Assert.Empty(results);
    }

    [Fact]
    public void Compute_ExactlyFifteenBars_EmitsOneSeedValueMatchingHandCalculation()
    {
        var bars = MakeSeedBars(15);

        var results = AtrCalculator.Compute(bars);

        var result = Assert.Single(results);
        Assert.Equal(14, result.Period);
        Assert.Equal(bars[14].Date, result.Date);
        Assert.Equal(2m, result.Value); // hand-calculated: sum(TR[1..14])/14 = 2, exact
    }

    [Fact]
    public void Compute_SixteenBarsWithGapOnLastDay_SecondValueMatchesHandCalculatedWildersSmoothing()
    {
        var bars = MakeSixteenBarsWithGapOnLastDay();

        var results = AtrCalculator.Compute(bars);

        Assert.Equal(2, results.Count);
        var second = results[1];
        Assert.Equal(bars[15].Date, second.Date);
        // hand-calculated: 18/7 = 2.571428571428571... - compared to 4 decimal places
        // since the true value is a non-terminating repeating decimal.
        Assert.Equal(2.5714m, second.Value, 4);
    }

    [Fact]
    public void Compute_TrueRangeDrivenByGapNotDailyRange_UsesTheLargerGapTerm()
    {
        // Isolates the max() logic: the gapped day's own High-Low range is only 2, but the
        // gap from the previous close (10) is what actually determines True Range - proves
        // the formula is genuinely max(highLow, |high-prevClose|, |low-prevClose|), not just
        // the daily range.
        var bars = MakeSixteenBarsWithGapOnLastDay();
        var dailyRangeOnGapDay = bars[15].High - bars[15].Low;

        var results = AtrCalculator.Compute(bars);
        var secondResult = results[1];

        Assert.Equal(2m, dailyRangeOnGapDay);
        // If True Range had used only the daily range (2) instead of the gap (10), ATR'
        // would be (2*13+2)/14 = 2.0 exactly, not ~2.5714.
        Assert.NotEqual(2.0000m, secondResult.Value);
    }

    [Fact]
    public void Compute_NoGapsAtAll_AtrEqualsConstantDailyRange()
    {
        // A pure sanity check with zero gaps anywhere: True Range collapses to the daily
        // range on every bar, so ATR stays exactly 2 through every subsequent value too.
        var bars = MakeSeedBars(20); // extends the gap-free pattern further

        var results = AtrCalculator.Compute(bars);

        Assert.All(results, r => Assert.Equal(2m, r.Value));
    }
}
