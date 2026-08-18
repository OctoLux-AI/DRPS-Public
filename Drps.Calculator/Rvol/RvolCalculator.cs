namespace Drps.Calculator.Rvol;

/// <summary>
/// Pure, deterministic RVOL (relative volume) computation - no DB/EF dependency, so it's
/// directly unit-testable. Anti-Spaghetti Rule #2: math lives in deterministic C#, not an
/// LLM. RVOL = current bar's Volume / average Volume over the trailing 20 bars, NOT
/// including the current bar in its own baseline average.
/// </summary>
public static class RvolCalculator
{
    public const int BaselineWindow = 20;

    // Baseline bars plus the current/anchor bar itself - the full span of bars that
    // participate in either the calculation (baseline) or the reporting date (current bar).
    // Used as the fixed window size for gap-detection, ex-dividend annotation, and
    // verification-join purposes - see RvolGapChecker's own doc comment.
    public const int WindowSize = BaselineWindow + 1;

    public readonly record struct RvolBarInput(DateOnly Date, long Volume);

    public readonly record struct RvolResult(DateOnly Date, decimal Value);

    /// <summary>
    /// Single forward pass over <paramref name="bars"/> (must already be ordered by date
    /// ascending), maintaining a rolling sum of the trailing BaselineWindow (20) bars'
    /// volume. A value is only emitted once at least WindowSize (21) bars are available -
    /// 20 to form the baseline plus the current bar itself. A bar whose trailing baseline
    /// average is zero (a genuinely volume-less baseline, e.g. a run of halted days) is
    /// skipped rather than dividing by zero or reporting a misleading sentinel - same
    /// exclude-rather-than-compute-garbage philosophy as BarReconciliationService's
    /// Close &lt;= 0 guard.
    /// </summary>
    public static IReadOnlyList<RvolResult> Compute(IReadOnlyList<RvolBarInput> bars)
    {
        var results = new List<RvolResult>();

        if (bars.Count < WindowSize)
        {
            return results;
        }

        long rollingBaselineSum = 0;
        for (var i = 0; i < BaselineWindow; i++)
        {
            rollingBaselineSum += bars[i].Volume;
        }

        for (var i = BaselineWindow; i < bars.Count; i++)
        {
            var baselineAverage = (decimal)rollingBaselineSum / BaselineWindow;

            if (baselineAverage > 0m)
            {
                var rvol = bars[i].Volume / baselineAverage;
                results.Add(new RvolResult(bars[i].Date, Math.Round(rvol, 4, MidpointRounding.AwayFromZero)));
            }

            // Advance the rolling baseline for the next iteration: bring in the bar that
            // just became "current" (it'll be part of the next bar's trailing baseline),
            // drop the oldest bar that's now outside the trailing 20.
            rollingBaselineSum += bars[i].Volume;
            rollingBaselineSum -= bars[i - BaselineWindow].Volume;
        }

        return results;
    }
}
