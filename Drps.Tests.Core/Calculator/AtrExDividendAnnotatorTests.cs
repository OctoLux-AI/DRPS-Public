using Drps.Calculator.Atr;

namespace Drps.Tests.Calculator;

/// <summary>
/// Mechanism-level tests only - the boundary/window-membership logic under test here is
/// identical to DmaExDividendAnnotatorTests/RsiExDividendAnnotatorTests (same shared
/// IndicatorWindowSpan helper, same inclusive-both-ends convention, same Period + 1 (15)
/// window size and reasoning as RsiGapCheckerTests). Unlike RvolExDividendAnnotatorTests,
/// ATR's flag is in the SAME justification category as DMA/RSI, not RVOL's weaker "worth
/// noting" reasoning: an ex-dividend date creates a genuinely inflated True Range on that
/// bar (the price gap itself shows up directly in |High - previousClose| and
/// |Low - previousClose|) - a real formula-distortion case, not just contextual
/// information about a coincidental volume spike. See AtrExDividendAnnotator's own doc
/// comment for the full explanation. That distinction is about interpretation for a future
/// consumer (Gate), not about this class's behavior, so it isn't separately testable here -
/// the boundary tests below are what actually verify correctness.
/// </summary>
public class AtrExDividendAnnotatorTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // Steady up-drift, constant range - annotation only cares about dates, not the ATR
    // value itself.
    private static List<AtrCalculator.AtrBarInput> MakeBars(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new AtrCalculator.AtrBarInput(FirstDay.AddDays(i), 102m + i, 100m + i, 101m + i))
            .ToList();

    [Fact]
    public void Annotate_ExDividendDateInsideWindowSpan_FlagsTrue()
    {
        var bars = MakeBars(15); // first (and only) ATR result at index14, window span [day0, day14]
        var results = AtrCalculator.Compute(bars);
        var firstResult = results.Single();

        var exDividendDates = new[] { FirstDay.AddDays(5) }; // squarely inside the span

        var annotated = AtrExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(firstResult));

        Assert.True(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateOutsideWindowSpan_FlagsFalse()
    {
        var bars = MakeBars(20);
        var results = AtrCalculator.Compute(bars);
        var firstResult = results.Single(r => r.Date == bars[14].Date); // window span [day0, day14]

        var exDividendDates = new[] { FirstDay.AddDays(18) }; // well after this result's own window end date

        var annotated = AtrExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(firstResult));

        Assert.False(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateExactlyOnWindowEndDate_FlagsTrue_BoundaryIsInclusive()
    {
        // Same deliberate boundary choice as DmaExDividendAnnotator/RsiExDividendAnnotator,
        // reused here rather than re-derived: an ex-div date landing exactly on the
        // window's anchor/end bar counts as "inside."
        var bars = MakeBars(15);
        var results = AtrCalculator.Compute(bars);
        var firstResult = results.Single(); // window span [day0, day14]

        var exDividendDates = new[] { FirstDay.AddDays(14) }; // exactly the window's end date

        var annotated = AtrExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(firstResult));

        Assert.True(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_ExDividendDateExactlyOnWindowStartDate_FlagsTrue_BoundaryIsInclusive()
    {
        var bars = MakeBars(15);
        var results = AtrCalculator.Compute(bars);
        var firstResult = results.Single(); // window span [day0, day14]

        var exDividendDates = new[] { FirstDay.AddDays(0) }; // exactly the window's start date

        var annotated = AtrExDividendAnnotator.Annotate(bars, results, exDividendDates);
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
        var results = AtrCalculator.Compute(bars);
        var resultAtIndex20 = results.Single(r => r.Date == bars[20].Date); // window span [day6, day20]

        var exDividendDates = new[] { FirstDay.AddDays(5) }; // one day before the window's start (day6)

        var annotated = AtrExDividendAnnotator.Annotate(bars, results, exDividendDates);
        var annotatedResult = annotated.Single(a => a.Result.Equals(resultAtIndex20));

        Assert.False(annotatedResult.HasExDividendEvent);
    }

    [Fact]
    public void Annotate_NoExDividendDatesAtAll_AllResultsFlagFalse()
    {
        var bars = MakeBars(40);
        var results = AtrCalculator.Compute(bars);

        var annotated = AtrExDividendAnnotator.Annotate(bars, results, Array.Empty<DateOnly>());

        Assert.All(annotated, a => Assert.False(a.HasExDividendEvent));
    }

    [Fact]
    public void Annotate_ExDividendDateAgesOutOfFixedTrailingWindow_LaterResultUnaffected()
    {
        var bars = MakeBars(30);
        var results = AtrCalculator.Compute(bars);
        var earlyResult = results.Single(r => r.Date == bars[14].Date); // window span [day0, day14]
        var lateResult = results.Single(r => r.Date == bars[29].Date); // window span [day15, day29]

        var exDividendDates = new[] { FirstDay.AddDays(2) };

        var annotated = AtrExDividendAnnotator.Annotate(bars, results, exDividendDates);

        Assert.True(annotated.Single(a => a.Result.Equals(earlyResult)).HasExDividendEvent);
        Assert.False(annotated.Single(a => a.Result.Equals(lateResult)).HasExDividendEvent);
    }
}
