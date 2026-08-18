using Drps.Calculator.Indicators;

namespace Drps.Calculator.Rsi;

/// <summary>
/// Pure, calendar-aware gap check layered on top of RsiSlopeCalculator's raw output - no
/// DB/HTTP dependency, directly unit-testable. Same discipline as RsiGapChecker/DmaGapChecker,
/// reusing their boundary logic via the shared IndicatorWindowSpan helper rather than
/// re-deriving it.
///
/// Operates over the stored RsiIndicator series' own dates (NOT raw bar dates) - a real
/// ingestion gap in the underlying bars may already have made RsiComputationService skip
/// writing a row for one or more dates (see RsiGapChecker), so consecutive RsiIndicator rows
/// are not guaranteed to be calendar-adjacent. A slope value at index i depends on the RSI
/// series' own dates from index i-Lookback through i (Lookback+1 dates) - this check verifies
/// no expected-open trading day inside that span is missing from the RSI series itself.
/// </summary>
public static class RsiSlopeGapChecker
{
    public readonly record struct SkippedResult(DateOnly Date, IReadOnlyList<DateOnly> MissingDates);

    public readonly record struct GapCheckResult(
        IReadOnlyList<RsiSlopeCalculator.RsiSlopeResult> ClearResults,
        IReadOnlyList<SkippedResult> SkippedResults);

    /// <summary>
    /// <paramref name="rsiSeries"/> must be the same ordered sequence passed to
    /// RsiSlopeCalculator.Compute. <paramref name="expectedOpenTradingDays"/> is the real
    /// trading calendar for (at least) the full [rsiSeries.First().Date,
    /// rsiSeries.Last().Date] range.
    /// </summary>
    public static GapCheckResult Filter(
        IReadOnlyList<RsiSlopeCalculator.RsiSlopeInput> rsiSeries,
        int lookback,
        IReadOnlySet<DateOnly> expectedOpenTradingDays)
    {
        var rawResults = RsiSlopeCalculator.Compute(rsiSeries, lookback);

        var rsiDates = rsiSeries.Select(r => r.Date).ToList();
        var dateToIndex = IndicatorWindowSpan.BuildDateIndex(rsiDates);
        var presentDates = (IReadOnlySet<DateOnly>)dateToIndex.Keys.ToHashSet();

        var windowSize = lookback + 1;

        var clear = new List<RsiSlopeCalculator.RsiSlopeResult>();
        var skipped = new List<SkippedResult>();

        foreach (var result in rawResults)
        {
            var endIndex = dateToIndex[result.Date];
            var span = IndicatorWindowSpan.GetWindowSpan(rsiDates, endIndex, windowSize);

            var missingDates = IndicatorWindowSpan.FindMissingTradingDays(span, expectedOpenTradingDays, presentDates);

            if (missingDates.Count > 0)
            {
                skipped.Add(new SkippedResult(result.Date, missingDates));
            }
            else
            {
                clear.Add(result);
            }
        }

        return new GapCheckResult(clear, skipped);
    }
}
