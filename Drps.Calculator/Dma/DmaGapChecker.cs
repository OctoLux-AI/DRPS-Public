namespace Drps.Calculator.Dma;

/// <summary>
/// Pure, calendar-aware gap check layered on top of DmaCalculator's raw output - no
/// DB/HTTP dependency, directly unit-testable. DmaCalculator.Compute builds windows purely
/// by index count over whatever verified bars exist; it has no notion of the real trading
/// calendar, so a window that is "N bars wide" by count can silently span more calendar
/// time than N trading days whenever an earlier bar is missing. This filters those windows
/// out rather than letting them compute over a wider-than-intended date range.
/// </summary>
public static class DmaGapChecker
{
    public readonly record struct SkippedWindow(DateOnly WindowEndDate, int Window, IReadOnlyList<DateOnly> MissingDates);

    public readonly record struct GapCheckResult(
        IReadOnlyList<DmaCalculator.DmaResult> ClearResults,
        IReadOnlyList<SkippedWindow> SkippedWindows);

    /// <summary>
    /// <paramref name="bars"/> must be the same ordered sequence passed to
    /// DmaCalculator.Compute. <paramref name="expectedOpenTradingDays"/> is the real
    /// trading calendar for (at least) the full [bars.First().Date, bars.Last().Date] range.
    /// A window is skipped when an expected-open trading day inside its date range has no
    /// corresponding bar - a real ingestion/verification gap, not a normal weekend/holiday
    /// closure (those dates are simply absent from expectedOpenTradingDays in the first
    /// place, so they're never checked).
    /// </summary>
    public static GapCheckResult Filter(
        IReadOnlyList<DmaCalculator.DmaBarInput> bars,
        IReadOnlySet<DateOnly> expectedOpenTradingDays)
    {
        var rawResults = DmaCalculator.Compute(bars);

        var dateToIndex = new Dictionary<DateOnly, int>();
        for (var i = 0; i < bars.Count; i++)
        {
            dateToIndex[bars[i].Date] = i;
        }

        var clear = new List<DmaCalculator.DmaResult>();
        var skipped = new List<SkippedWindow>();

        foreach (var result in rawResults)
        {
            var endIndex = dateToIndex[result.Date];
            var startDate = bars[endIndex - result.Window + 1].Date;
            var endDate = result.Date;

            var missingDates = expectedOpenTradingDays
                .Where(d => d >= startDate && d <= endDate && !dateToIndex.ContainsKey(d))
                .OrderBy(d => d)
                .ToList();

            if (missingDates.Count > 0)
            {
                skipped.Add(new SkippedWindow(endDate, result.Window, missingDates));
            }
            else
            {
                clear.Add(result);
            }
        }

        return new GapCheckResult(clear, skipped);
    }
}
