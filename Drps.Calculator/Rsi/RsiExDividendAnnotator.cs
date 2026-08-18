using Drps.Calculator.Indicators;

namespace Drps.Calculator.Rsi;

/// <summary>
/// Pure, informational annotation layered on top of RsiCalculator's raw output - no
/// DB/HTTP dependency, directly unit-testable. Same category as DmaExDividendAnnotator: per
/// CLAUDE.md's Ex-Dividend Handling decision (2026-07-14), raw prices stay unadjusted
/// everywhere, so a window spanning an ex-dividend date will show a mechanical price drop
/// that isn't a real signal for RSI's gain/loss math either. Purely a data-quality tag - it
/// never skips, excludes, or alters a computed value.
///
/// Reuses DmaExDividendAnnotator's exact boundary convention (inclusive both ends) via the
/// shared IndicatorWindowSpan helper, rather than re-deriving it. See RsiGapChecker's doc
/// comment for why the window size used here is Period + 1 (15), not Period (14).
/// </summary>
public static class RsiExDividendAnnotator
{
    public readonly record struct AnnotatedResult(RsiCalculator.RsiResult Result, bool HasExDividendEvent);

    public static IReadOnlyList<AnnotatedResult> Annotate(
        IReadOnlyList<RsiCalculator.RsiBarInput> bars,
        IReadOnlyList<RsiCalculator.RsiResult> results,
        IReadOnlyCollection<DateOnly> exDividendDates)
    {
        var barDates = bars.Select(b => b.Date).ToList();
        var dateToIndex = IndicatorWindowSpan.BuildDateIndex(barDates);

        var annotated = new List<AnnotatedResult>();

        foreach (var result in results)
        {
            var endIndex = dateToIndex[result.Date];
            var span = IndicatorWindowSpan.GetWindowSpan(barDates, endIndex, RsiGapChecker.WindowSize);

            var hasExDividendEvent = IndicatorWindowSpan.ContainsAny(span, exDividendDates);

            annotated.Add(new AnnotatedResult(result, hasExDividendEvent));
        }

        return annotated;
    }
}
