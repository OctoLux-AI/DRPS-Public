using Drps.Calculator.Indicators;

namespace Drps.Calculator.Rsi;

/// <summary>
/// Pure, calendar-aware gap check layered on top of RsiCalculator's raw output - no
/// DB/HTTP dependency, directly unit-testable. Same discipline as DmaGapChecker, reusing
/// its boundary logic via the shared IndicatorWindowSpan helper rather than re-deriving it.
///
/// Deliberate simplification, stated plainly: Wilder's RSI is a cumulatively-smoothed
/// running average with an unbounded true dependency chain (see RsiCalculator's own doc
/// comment) - a fully rigorous gap check would have to verify every bar back to the
/// series' first value. This codebase instead checks a fixed trailing window of
/// Period + 1 (15) bars - the same bar count Wilder's own seed calculation requires for its
/// first value - consistent with the task's explicit framing of "the 14-bar window" and
/// with DMA's fixed-window gap-check convention. A gap further back than that trailing
/// window (in an already-smoothed-through region) will not be caught by this check.
/// </summary>
public static class RsiGapChecker
{
    // Matches the true minimum data dependency of Wilder's seed average (14 deltas require
    // 15 closes) - see RsiCalculator.Compute. Used as a constant trailing-window size for
    // every emitted RSI value, not just the first.
    public const int WindowSize = RsiCalculator.Period + 1;

    public readonly record struct SkippedResult(DateOnly Date, IReadOnlyList<DateOnly> MissingDates);

    public readonly record struct GapCheckResult(
        IReadOnlyList<RsiCalculator.RsiResult> ClearResults,
        IReadOnlyList<SkippedResult> SkippedResults);

    /// <summary>
    /// <paramref name="bars"/> must be the same ordered sequence passed to
    /// RsiCalculator.Compute. <paramref name="expectedOpenTradingDays"/> is the real trading
    /// calendar for (at least) the full [bars.First().Date, bars.Last().Date] range.
    /// </summary>
    public static GapCheckResult Filter(
        IReadOnlyList<RsiCalculator.RsiBarInput> bars,
        IReadOnlySet<DateOnly> expectedOpenTradingDays)
    {
        var rawResults = RsiCalculator.Compute(bars);

        var barDates = bars.Select(b => b.Date).ToList();
        var dateToIndex = IndicatorWindowSpan.BuildDateIndex(barDates);
        var presentDates = (IReadOnlySet<DateOnly>)dateToIndex.Keys.ToHashSet();

        var clear = new List<RsiCalculator.RsiResult>();
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
