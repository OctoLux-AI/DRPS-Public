using Drps.Calculator.Rsi;

namespace Drps.Tests.Calculator;

public class RsiExDividendAnnotatorTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // closes = 1, 2, 3, ... - same convention as RsiGapCheckerTests; annotation only cares
    // about dates, not the RSI value itself.
    private static List<RsiCalculator.RsiBarInput> MakeBars(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new RsiCalculator.RsiBarInput(FirstDay.AddDays(i), i + 1m))
            .ToList();

    [Fact]
    public void Annotate_ExDividendDateInsideWindowSpan_FlagsTrue()
    {
        var bars = MakeBars(15); // first (and only) RSI result at index14, window span [day0, day14]
        var results = RsiCalculator.Compute(bars);
        var firstResult = results.Single();

        var exDividendDates = new[] { FirstDay.AddDays(5) }; // squarely inside the span

        var annotated = RsiExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(firstResult));

        Assert.True(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateOutsideWindowSpan_FlagsFalse()
    {
        var bars = MakeBars(20);
        var results = RsiCalculator.Compute(bars);
        var firstResult = results.Single(r => r.Date == bars[14].Date); // window span [day0, day14]

        var exDividendDates = new[] { FirstDay.AddDays(18) }; // well after this result's own window end date

        var annotated = RsiExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(firstResult));

        Assert.False(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateExactlyOnWindowEndDate_FlagsTrue_BoundaryIsInclusive()
    {
        // Same deliberate boundary choice as DmaExDividendAnnotator, reused here rather than
        // re-derived: an ex-div date landing exactly on the window's anchor/end bar counts
        // as "inside."
        var bars = MakeBars(15);
        var results = RsiCalculator.Compute(bars);
        var firstResult = results.Single(); // window span [day0, day14]

        var exDividendDates = new[] { FirstDay.AddDays(14) }; // exactly the window's end date

        var annotated = RsiExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(firstResult));

        Assert.True(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateExactlyOnWindowStartDate_FlagsTrue_BoundaryIsInclusive()
    {
        var bars = MakeBars(15);
        var results = RsiCalculator.Compute(bars);
        var firstResult = results.Single(); // window span [day0, day14]

        var exDividendDates = new[] { FirstDay.AddDays(0) }; // exactly the window's start date

        var annotated = RsiExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(firstResult));

        Assert.True(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateOneDayBeforeWindowStart_FlagsFalse()
    {
        // A later result whose window has moved past the earlier dates - complements the
        // boundary tests above by confirming a date genuinely outside the window (not just
        // "not a bar date") is never flagged.
        var bars = MakeBars(30);
        var results = RsiCalculator.Compute(bars);
        var resultAtIndex20 = results.Single(r => r.Date == bars[20].Date); // window span [day6, day20]

        var exDividendDates = new[] { FirstDay.AddDays(5) }; // one day before the window's start (day6)

        var annotated = RsiExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(resultAtIndex20));

        Assert.False(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_NoExDividendDatesAtAll_AllResultsFlagFalse()
    {
        var bars = MakeBars(40);
        var results = RsiCalculator.Compute(bars);

        var annotated = RsiExDividendAnnotator.Annotate(bars, results, Array.Empty<DateOnly>());

        Assert.All(annotated, a => Assert.False(a.HasExDividendEvent));
    }

    [Fact]
    public void Annotate_ExDividendDateAgesOutOfFixedTrailingWindow_LaterResultUnaffected()
    {
        // RSI has one fixed window size (unlike DMA's four), so the analogous
        // "independent per window" case here is "independent per result over time": an
        // ex-div date near the start of the series flags an early result (whose trailing
        // window still reaches it) but not a later one (once the fixed window has moved
        // past it) - documents the same deliberate simplification as RsiGapChecker.
        var bars = MakeBars(30);
        var results = RsiCalculator.Compute(bars);
        var earlyResult = results.Single(r => r.Date == bars[14].Date); // window span [day0, day14]
        var lateResult = results.Single(r => r.Date == bars[29].Date); // window span [day15, day29]

        var exDividendDates = new[] { FirstDay.AddDays(2) };

        var annotated = RsiExDividendAnnotator.Annotate(bars, results, exDividendDates);

        Assert.True(annotated.Single(a => a.Result.Equals(earlyResult)).HasExDividendEvent);
        Assert.False(annotated.Single(a => a.Result.Equals(lateResult)).HasExDividendEvent);
    }
}
