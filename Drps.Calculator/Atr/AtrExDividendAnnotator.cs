using Drps.Calculator.Indicators;

namespace Drps.Calculator.Atr;

/// <summary>
/// Pure, informational annotation layered on top of AtrCalculator's raw output - no
/// DB/HTTP dependency, directly unit-testable. Reuses DmaExDividendAnnotator/
/// RsiExDividendAnnotator's exact boundary convention (inclusive both ends) via the shared
/// IndicatorWindowSpan helper, rather than re-deriving it.
///
/// Same justification category as DMA/RSI, NOT RVOL's weaker "worth noting" reasoning: an
/// ex-dividend date creates a mechanically large True Range on that bar (the price gap
/// itself shows up directly in |High - previousClose| / |Low - previousClose|), which
/// inflates ATR artificially - a genuine formula-distortion case, the same way an
/// unadjusted ex-div price drop distorts a Close-based DMA/RSI value. This is purely a
/// data-quality tag (never skips/excludes/alters the computed value), same mechanism as
/// every other indicator's flag - only the reason differs from RVOL's.
/// </summary>
public static class AtrExDividendAnnotator
{
    public readonly record struct AnnotatedResult(AtrCalculator.AtrResult Result, bool HasExDividendEvent);

    public static IReadOnlyList<AnnotatedResult> Annotate(
        IReadOnlyList<AtrCalculator.AtrBarInput> bars,
        IReadOnlyList<AtrCalculator.AtrResult> results,
        IReadOnlyCollection<DateOnly> exDividendDates)
    {
        var barDates = bars.Select(b => b.Date).ToList();
        var dateToIndex = IndicatorWindowSpan.BuildDateIndex(barDates);

        var annotated = new List<AnnotatedResult>();

        foreach (var result in results)
        {
            var endIndex = dateToIndex[result.Date];
            var span = IndicatorWindowSpan.GetWindowSpan(barDates, endIndex, AtrGapChecker.WindowSize);

            var hasExDividendEvent = IndicatorWindowSpan.ContainsAny(span, exDividendDates);

            annotated.Add(new AnnotatedResult(result, hasExDividendEvent));
        }

        return annotated;
    }
}
