using Drps.Calculator.Indicators;

namespace Drps.Calculator.Rvol;

/// <summary>
/// Pure, informational annotation layered on top of RvolCalculator's raw output - no
/// DB/HTTP dependency, directly unit-testable. Reuses DmaExDividendAnnotator/
/// RsiExDividendAnnotator's exact boundary convention (inclusive both ends) via the shared
/// IndicatorWindowSpan helper, rather than re-deriving it.
///
/// IMPORTANT - the REASON this flag exists differs from DMA/RSI, even though the mechanism
/// (window-span membership check) is identical: DMA and RSI are both computed from Close
/// price, and raw prices stay unadjusted for ex-dividend everywhere in this codebase, so
/// those flags exist because of a genuine mechanical-formula distortion (the ex-div price
/// drop is baked into the raw Close, but isn't a real signal). Volume is NOT computed from
/// price at all - there is no mechanical distortion here. This flag instead exists because
/// a volume spike coinciding with an ex-dividend date may be corporate-action-driven
/// (income/fund trading, arbitrage, or dividend-capture activity around the date) rather
/// than a genuine breakout signal. Same data-quality-tag category as the DMA/RSI flags and
/// just as purely informational (never skips/excludes/alters the computed value) - but a
/// future consumer (Gate) should read this one as "context for interpreting a volume
/// spike," not "this number might be mechanically wrong" like the price-based indicators.
/// </summary>
public static class RvolExDividendAnnotator
{
    public readonly record struct AnnotatedResult(RvolCalculator.RvolResult Result, bool HasExDividendEvent);

    public static IReadOnlyList<AnnotatedResult> Annotate(
        IReadOnlyList<RvolCalculator.RvolBarInput> bars,
        IReadOnlyList<RvolCalculator.RvolResult> results,
        IReadOnlyCollection<DateOnly> exDividendDates)
    {
        var barDates = bars.Select(b => b.Date).ToList();
        var dateToIndex = IndicatorWindowSpan.BuildDateIndex(barDates);

        var annotated = new List<AnnotatedResult>();

        foreach (var result in results)
        {
            var endIndex = dateToIndex[result.Date];
            var span = IndicatorWindowSpan.GetWindowSpan(barDates, endIndex, RvolCalculator.WindowSize);

            var hasExDividendEvent = IndicatorWindowSpan.ContainsAny(span, exDividendDates);

            annotated.Add(new AnnotatedResult(result, hasExDividendEvent));
        }

        return annotated;
    }
}
