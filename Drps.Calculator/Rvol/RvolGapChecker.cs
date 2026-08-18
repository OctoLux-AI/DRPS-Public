using Drps.Calculator.Indicators;

namespace Drps.Calculator.Rvol;

/// <summary>
/// Pure, calendar-aware gap check layered on top of RvolCalculator's raw output - no
/// DB/HTTP dependency, directly unit-testable. Same discipline as DmaGapChecker/
/// RsiGapChecker, reusing the shared IndicatorWindowSpan helper rather than re-deriving
/// boundary behavior.
///
/// Window size here is RvolCalculator.WindowSize (21) - the 20-bar trailing baseline plus
/// the current/anchor bar itself, span [bars[endIndex-20], bars[endIndex]]. A missing
/// expected trading day anywhere in that 21-bar span (task explicitly says "within the
/// 20-bar baseline window") fails the whole RVOL value closed - it's skipped, not
/// interpolated.
/// </summary>
public static class RvolGapChecker
{
    public readonly record struct SkippedResult(DateOnly Date, IReadOnlyList<DateOnly> MissingDates);

    public readonly record struct GapCheckResult(
        IReadOnlyList<RvolCalculator.RvolResult> ClearResults,
        IReadOnlyList<SkippedResult> SkippedResults);

    /// <summary>
    /// <paramref name="bars"/> must be the same ordered sequence passed to
    /// RvolCalculator.Compute. <paramref name="expectedOpenTradingDays"/> is the real
    /// trading calendar for (at least) the full [bars.First().Date, bars.Last().Date] range.
    /// </summary>
    public static GapCheckResult Filter(
        IReadOnlyList<RvolCalculator.RvolBarInput> bars,
        IReadOnlySet<DateOnly> expectedOpenTradingDays)
    {
        var rawResults = RvolCalculator.Compute(bars);

        var barDates = bars.Select(b => b.Date).ToList();
        var dateToIndex = IndicatorWindowSpan.BuildDateIndex(barDates);
        var presentDates = (IReadOnlySet<DateOnly>)dateToIndex.Keys.ToHashSet();

        var clear = new List<RvolCalculator.RvolResult>();
        var skipped = new List<SkippedResult>();

        foreach (var result in rawResults)
        {
            var endIndex = dateToIndex[result.Date];
            var span = IndicatorWindowSpan.GetWindowSpan(barDates, endIndex, RvolCalculator.WindowSize);

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
