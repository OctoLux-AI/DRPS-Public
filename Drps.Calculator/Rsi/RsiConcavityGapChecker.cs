using Drps.Calculator.Indicators;

namespace Drps.Calculator.Rsi;

/// <summary>
/// Pure, calendar-aware gap check layered on top of RsiConcavityCalculator's raw output - no
/// DB/HTTP dependency, directly unit-testable. Same discipline as RsiSlopeGapChecker, applied
/// one level up: operates over the stored RsiSlopeIndicator series' own dates (NOT RSI dates,
/// NOT raw bar dates).
///
/// A concavity value at index i depends on exactly two consecutive slope readings (index i-1
/// and i) - fixed window size of 2, unlike RsiSlopeGapChecker's configurable Lookback+1. This
/// check verifies those two slope dates are truly calendar-adjacent (no expected-open trading
/// day was silently skipped between them because an intervening slope value itself failed its
/// own gap check).
/// </summary>
public static class RsiConcavityGapChecker
{
    // Exactly two slope readings participate in any one concavity value - see this class's own
    // doc comment.
    public const int WindowSize = 2;

    public readonly record struct SkippedResult(DateOnly Date, IReadOnlyList<DateOnly> MissingDates);

    public readonly record struct GapCheckResult(
        IReadOnlyList<RsiConcavityCalculator.RsiConcavityResult> ClearResults,
        IReadOnlyList<SkippedResult> SkippedResults);

    /// <summary>
    /// <paramref name="slopeSeries"/> must be the same ordered sequence passed to
    /// RsiConcavityCalculator.Compute. <paramref name="expectedOpenTradingDays"/> is the real
    /// trading calendar for (at least) the full [slopeSeries.First().Date,
    /// slopeSeries.Last().Date] range.
    /// </summary>
    public static GapCheckResult Filter(
        IReadOnlyList<RsiConcavityCalculator.RsiConcavityInput> slopeSeries,
        IReadOnlySet<DateOnly> expectedOpenTradingDays)
    {
        var rawResults = RsiConcavityCalculator.Compute(slopeSeries);

        var slopeDates = slopeSeries.Select(s => s.Date).ToList();
        var dateToIndex = IndicatorWindowSpan.BuildDateIndex(slopeDates);
        var presentDates = (IReadOnlySet<DateOnly>)dateToIndex.Keys.ToHashSet();

        var clear = new List<RsiConcavityCalculator.RsiConcavityResult>();
        var skipped = new List<SkippedResult>();

        foreach (var result in rawResults)
        {
            var endIndex = dateToIndex[result.Date];
            var span = IndicatorWindowSpan.GetWindowSpan(slopeDates, endIndex, WindowSize);

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
