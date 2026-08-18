using Drps.Calculator.Rvol;

namespace Drps.Tests.Calculator;

/// <summary>
/// Mechanism-level tests only - the boundary/window-membership logic under test here is
/// identical to DmaExDividendAnnotatorTests/RsiExDividendAnnotatorTests (same shared
/// IndicatorWindowSpan helper, same inclusive-both-ends convention). What's genuinely
/// different for RVOL is the *reason* the flag exists, not how it's computed: DMA/RSI flag
/// a possible mechanical-formula distortion in a price-derived value, while RVOL flags that
/// a volume spike coinciding with an ex-dividend date may be corporate-action-driven
/// (income/fund trading around the date) rather than a genuine breakout signal - see
/// RvolExDividendAnnotator's own doc comment for the full explanation. That distinction is
/// about interpretation for a future consumer (Gate), not about this class's behavior, so
/// it isn't separately testable here - the boundary tests below are what actually verify
/// correctness.
/// </summary>
public class RvolExDividendAnnotatorTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // Constant volume - annotation only cares about dates, not the RVOL value itself.
    private static List<RvolCalculator.RvolBarInput> MakeBars(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new RvolCalculator.RvolBarInput(FirstDay.AddDays(i), 1000L))
            .ToList();

    [Fact]
    public void Annotate_ExDividendDateInsideWindowSpan_FlagsTrue()
    {
        var bars = MakeBars(21); // first (and only) RVOL result at index20, window span [day0, day20]
        var results = RvolCalculator.Compute(bars);
        var firstResult = results.Single();

        var exDividendDates = new[] { FirstDay.AddDays(10) }; // squarely inside the span

        var annotated = RvolExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(firstResult));

        Assert.True(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateOutsideWindowSpan_FlagsFalse()
    {
        var bars = MakeBars(30);
        var results = RvolCalculator.Compute(bars);
        var firstResult = results.Single(r => r.Date == bars[20].Date); // window span [day0, day20]

        var exDividendDates = new[] { FirstDay.AddDays(25) }; // well after this result's own window end date

        var annotated = RvolExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(firstResult));

        Assert.False(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateExactlyOnWindowEndDate_FlagsTrue_BoundaryIsInclusive()
    {
        // Same deliberate boundary choice as DmaExDividendAnnotator/RsiExDividendAnnotator,
        // reused here rather than re-derived: an ex-div date landing exactly on the
        // window's anchor/end bar (the current bar itself) counts as "inside."
        var bars = MakeBars(21);
        var results = RvolCalculator.Compute(bars);
        var firstResult = results.Single(); // window span [day0, day20]

        var exDividendDates = new[] { FirstDay.AddDays(20) }; // exactly the window's end date

        var annotated = RvolExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(firstResult));

        Assert.True(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateExactlyOnWindowStartDate_FlagsTrue_BoundaryIsInclusive()
    {
        var bars = MakeBars(21);
        var results = RvolCalculator.Compute(bars);
        var firstResult = results.Single(); // window span [day0, day20]

        var exDividendDates = new[] { FirstDay.AddDays(0) }; // exactly the window's start date

        var annotated = RvolExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(firstResult));

        Assert.True(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateOneDayBeforeWindowStart_FlagsFalse()
    {
        // A later result whose window has moved past the earlier dates - complements the
        // boundary tests above by confirming a date genuinely outside the window (not just
        // "not a bar date") is never flagged.
        var bars = MakeBars(40);
        var results = RvolCalculator.Compute(bars);
        var resultAtIndex30 = results.Single(r => r.Date == bars[30].Date); // window span [day10, day30]

        var exDividendDates = new[] { FirstDay.AddDays(9) }; // one day before the window's start (day10)

        var annotated = RvolExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(resultAtIndex30));

        Assert.False(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_NoExDividendDatesAtAll_AllResultsFlagFalse()
    {
        var bars = MakeBars(40);
        var results = RvolCalculator.Compute(bars);

        var annotated = RvolExDividendAnnotator.Annotate(bars, results, Array.Empty<DateOnly>());

        Assert.All(annotated, a => Assert.False(a.HasExDividendEvent));
    }

    [Fact]
    public void Annotate_ExDividendDateAgesOutOfFixedTrailingWindow_LaterResultUnaffected()
    {
        var bars = MakeBars(45);
        var results = RvolCalculator.Compute(bars);
        var earlyResult = results.Single(r => r.Date == bars[20].Date); // window span [day0, day20]
        var lateResult = results.Single(r => r.Date == bars[44].Date); // window span [day24, day44]

        var exDividendDates = new[] { FirstDay.AddDays(2) };

        var annotated = RvolExDividendAnnotator.Annotate(bars, results, exDividendDates);

        Assert.True(annotated.Single(a => a.Result.Equals(earlyResult)).HasExDividendEvent);
        Assert.False(annotated.Single(a => a.Result.Equals(lateResult)).HasExDividendEvent);
    }
}
