namespace Drps.Calculator.Dma;

/// <summary>
/// Pure, informational annotation layered on top of DmaCalculator's raw output - no
/// DB/HTTP dependency, directly unit-testable. Per CLAUDE.md's Ex-Dividend Handling
/// decision (2026-07-14): raw prices stay unadjusted everywhere, so a window spanning an
/// ex-dividend date will show a mechanical price drop that isn't a real signal. This is
/// purely a data-quality tag, same category as Verified - it never skips, excludes, or
/// alters a window's computed value. Gap-detection/verification-join logic from prior
/// tasks is completely separate and untouched by this.
/// </summary>
public static class DmaExDividendAnnotator
{
    public readonly record struct AnnotatedResult(DmaCalculator.DmaResult Result, bool HasExDividendEvent);

    /// <summary>
    /// <paramref name="bars"/> must be the same ordered sequence passed to
    /// DmaCalculator.Compute, so each result's window boundaries can be reconstructed by bar
    /// index - same approach DmaGapChecker already uses and for the same reason: a
    /// calendar-day range would misalign whenever a weekend/holiday falls inside the window.
    /// A window's date span is inclusive on both ends ([windowStartDate, windowEndDate]) -
    /// an ex-dividend date landing exactly on either boundary bar counts as "inside" the
    /// window and sets the flag true, since that bar's price genuinely reflects the
    /// mechanical drop and is genuinely one of the bars averaged into this window's value.
    /// </summary>
    public static IReadOnlyList<AnnotatedResult> Annotate(
        IReadOnlyList<DmaCalculator.DmaBarInput> bars,
        IReadOnlyList<DmaCalculator.DmaResult> results,
        IReadOnlyCollection<DateOnly> exDividendDates)
    {
        var dateToIndex = new Dictionary<DateOnly, int>();
        for (var i = 0; i < bars.Count; i++)
        {
            dateToIndex[bars[i].Date] = i;
        }

        var annotated = new List<AnnotatedResult>();

        foreach (var result in results)
        {
            var endIndex = dateToIndex[result.Date];
            var windowStartDate = bars[endIndex - result.Window + 1].Date;
            var windowEndDate = result.Date;

            var hasExDividendEvent = exDividendDates.Any(d => d >= windowStartDate && d <= windowEndDate);

            annotated.Add(new AnnotatedResult(result, hasExDividendEvent));
        }

        return annotated;
    }
}
