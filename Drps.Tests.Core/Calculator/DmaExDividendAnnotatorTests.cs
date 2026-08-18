using Drps.Calculator.Dma;

namespace Drps.Tests.Calculator;

public class DmaExDividendAnnotatorTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // closes = 1, 2, 3, ... - same convention as DmaCalculatorTests/DmaGapCheckerTests.
    private static List<DmaCalculator.DmaBarInput> MakeBars(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new DmaCalculator.DmaBarInput(FirstDay.AddDays(i), i + 1m))
            .ToList();

    [Fact]
    public void Annotate_ExDividendDateInsideWindowSpan_FlagsTrue()
    {
        var bars = MakeBars(10);
        var results = DmaCalculator.Compute(bars);
        var dma5AtOffset4 = results.Single(r => r.Window == 5 && r.Date == bars[4].Date); // window span [offset0, offset4]

        var exDividendDates = new[] { FirstDay.AddDays(2) }; // squarely inside the span

        var annotated = DmaExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(dma5AtOffset4));

        Assert.True(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateOutsideWindowSpan_FlagsFalse()
    {
        var bars = MakeBars(10);
        var results = DmaCalculator.Compute(bars);
        var dma5AtOffset4 = results.Single(r => r.Window == 5 && r.Date == bars[4].Date); // window span [offset0, offset4]

        var exDividendDates = new[] { FirstDay.AddDays(6) }; // after this window's own end date

        var annotated = DmaExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(dma5AtOffset4));

        Assert.False(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateExactlyOnWindowEndDate_FlagsTrue_BoundaryIsInclusive()
    {
        // Documents the deliberate boundary choice: an ex-div date landing exactly on the
        // window's anchor/end bar counts as "inside" - that bar's price genuinely reflects
        // the mechanical drop and is genuinely one of the bars averaged into this value.
        var bars = MakeBars(10);
        var results = DmaCalculator.Compute(bars);
        var dma5AtOffset4 = results.Single(r => r.Window == 5 && r.Date == bars[4].Date); // window span [offset0, offset4]

        var exDividendDates = new[] { FirstDay.AddDays(4) }; // exactly the window's end date

        var annotated = DmaExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(dma5AtOffset4));

        Assert.True(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateExactlyOnWindowStartDate_FlagsTrue_BoundaryIsInclusive()
    {
        // The other boundary: the window's earliest bar is included on the same inclusive
        // basis as the end date above - both ends of [windowStartDate, windowEndDate] count.
        var bars = MakeBars(10);
        var results = DmaCalculator.Compute(bars);
        var dma5AtOffset4 = results.Single(r => r.Window == 5 && r.Date == bars[4].Date); // window span [offset0, offset4]

        var exDividendDates = new[] { FirstDay.AddDays(0) }; // exactly the window's start date

        var annotated = DmaExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(dma5AtOffset4));

        Assert.True(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateOneDayBeforeWindowStart_FlagsFalse()
    {
        // Complements the boundary tests above: a date genuinely outside the window (not
        // just "not one of its bar dates") must not be flagged - proves the boundary is
        // exact, not fuzzy.
        var bars = MakeBars(15);
        var results = DmaCalculator.Compute(bars);
        var dma5AtOffset9 = results.Single(r => r.Window == 5 && r.Date == bars[9].Date); // window span [offset5, offset9]

        var exDividendDates = new[] { FirstDay.AddDays(4) }; // one day before the window's start (offset5)

        var annotated = DmaExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(dma5AtOffset9));

        Assert.False(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_NoExDividendDatesAtAll_AllWindowsFlagFalse()
    {
        var bars = MakeBars(70);
        var results = DmaCalculator.Compute(bars);

        var annotated = DmaExDividendAnnotator.Annotate(bars, results, Array.Empty<DateOnly>());

        Assert.All(annotated, a => Assert.False(a.HasExDividendEvent));
    }

    [Fact]
    public void Annotate_DateOnlyReachableByWiderWindow_FlagsOnlyThatWindowForTheSameBar()
    {
        // A single ex-div date near the start of a longer series should flag DMA-60 (whose
        // wide span reaches back far enough) for the last bar, while DMA-5/15/30 for that
        // same bar (narrower spans, don't reach back that far) stay unaffected - confirms
        // annotation is evaluated per-window independently, not per-bar.
        var bars = MakeBars(70);
        var results = DmaCalculator.Compute(bars);
        var lastDate = bars[^1].Date; // offset 69

        // DMA-60 for the last bar spans [offset10, offset69]; DMA-5/15/30 span
        // [offset65,69], [offset55,69], [offset40,69] respectively - offset15 falls only
        // inside the first.
        var exDividendDates = new[] { FirstDay.AddDays(15) };

        var annotated = DmaExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var forLastBar = annotated
            .Where(a => a.Result.Date == lastDate)
            .ToDictionary(a => a.Result.Window, a => a.HasExDividendEvent);

        Assert.True(forLastBar[60]);
        Assert.False(forLastBar[30]);
        Assert.False(forLastBar[15]);
        Assert.False(forLastBar[5]);
    }
}
