namespace Drps.Calculator.Indicators;

/// <summary>
/// Shared, indicator-agnostic window-boundary reconstruction, used by both DMA's and RSI's
/// gap-detection and ex-dividend-annotation logic. A computed value's window is
/// reconstructed by BAR INDEX COUNT (not calendar-day arithmetic), since a calendar-day
/// range would misalign whenever a weekend/holiday falls inside the window - the same
/// reasoning DmaGapChecker/DmaExDividendAnnotator established. A window's date span is
/// inclusive on both ends ([Start, End]) - the single boundary convention every indicator
/// in this codebase uses, confirmed and tested for DMA and reused here rather than
/// re-derived.
/// </summary>
public static class IndicatorWindowSpan
{
    public readonly record struct DateSpan(DateOnly Start, DateOnly End);

    public static Dictionary<DateOnly, int> BuildDateIndex(IReadOnlyList<DateOnly> barDates)
    {
        var index = new Dictionary<DateOnly, int>();
        for (var i = 0; i < barDates.Count; i++)
        {
            index[barDates[i]] = i;
        }

        return index;
    }

    /// <summary>
    /// [barDates[endIndex - windowSize + 1], barDates[endIndex]], inclusive both ends.
    /// </summary>
    public static DateSpan GetWindowSpan(IReadOnlyList<DateOnly> barDates, int endIndex, int windowSize) =>
        new(barDates[endIndex - windowSize + 1], barDates[endIndex]);

    /// <summary>
    /// Expected-open trading days inside <paramref name="span"/> that have no corresponding
    /// bar - a real ingestion/verification gap, not a normal weekend/holiday closure (those
    /// dates are simply absent from <paramref name="expectedOpenTradingDays"/> in the first
    /// place, so they're never checked).
    /// </summary>
    public static IReadOnlyList<DateOnly> FindMissingTradingDays(
        DateSpan span, IReadOnlySet<DateOnly> expectedOpenTradingDays, IReadOnlySet<DateOnly> presentDates) =>
        expectedOpenTradingDays
            .Where(d => d >= span.Start && d <= span.End && !presentDates.Contains(d))
            .OrderBy(d => d)
            .ToList();

    /// <summary>
    /// True if any of <paramref name="candidateDates"/> falls within <paramref name="span"/>.
    /// </summary>
    public static bool ContainsAny(DateSpan span, IReadOnlyCollection<DateOnly> candidateDates) =>
        candidateDates.Any(d => d >= span.Start && d <= span.End);
}
