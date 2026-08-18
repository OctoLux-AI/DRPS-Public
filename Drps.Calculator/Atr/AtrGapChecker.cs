using Drps.Calculator.Indicators;

namespace Drps.Calculator.Atr;

/// <summary>
/// Pure, calendar-aware gap check layered on top of AtrCalculator's raw output - no
/// DB/HTTP dependency, directly unit-testable. Same discipline as DmaGapChecker/
/// RsiGapChecker, reusing the shared IndicatorWindowSpan helper rather than re-deriving it.
///
/// True Range's own previous-Close dependency makes this especially important to get
/// right: a missing bar doesn't just shrink the window by one entry the way it would for a
/// simple average - it also means the True Range calculated for the bar immediately after
/// the gap is measured against a previous Close that's more than one trading day old,
/// silently spanning more real calendar time than a single day's true range should. Same
/// deliberate simplification as RsiGapChecker, stated plainly: Wilder's ATR is a
/// cumulatively-smoothed running average with an unbounded true dependency chain, but this
/// codebase checks a fixed trailing window of Period + 1 (15) bars - the same bar count
/// Wilder's own seed calculation requires for its first value.
/// </summary>
public static class AtrGapChecker
{
    // Matches the true minimum data dependency of Wilder's seed average (14 true ranges
    // require 15 closes) - see AtrCalculator.Compute.
    public const int WindowSize = AtrCalculator.Period + 1;

    public readonly record struct SkippedResult(DateOnly Date, IReadOnlyList<DateOnly> MissingDates);

    public readonly record struct GapCheckResult(
        IReadOnlyList<AtrCalculator.AtrResult> ClearResults,
        IReadOnlyList<SkippedResult> SkippedResults);

    /// <summary>
    /// <paramref name="bars"/> must be the same ordered sequence passed to
    /// AtrCalculator.Compute. <paramref name="expectedOpenTradingDays"/> is the real trading
    /// calendar for (at least) the full [bars.First().Date, bars.Last().Date] range.
    /// </summary>
    public static GapCheckResult Filter(
        IReadOnlyList<AtrCalculator.AtrBarInput> bars,
        IReadOnlySet<DateOnly> expectedOpenTradingDays)
    {
        var rawResults = AtrCalculator.Compute(bars);

        var barDates = bars.Select(b => b.Date).ToList();
        var dateToIndex = IndicatorWindowSpan.BuildDateIndex(barDates);
        var presentDates = (IReadOnlySet<DateOnly>)dateToIndex.Keys.ToHashSet();

        var clear = new List<AtrCalculator.AtrResult>();
        var skipped = new List<SkippedResult>();

        foreach (var result in rawResults)
        {
            var endIndex = dateToIndex[result.Date];
            var span = IndicatorWindowSpan.GetWindowSpan(barDates, endIndex, WindowSize);

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
