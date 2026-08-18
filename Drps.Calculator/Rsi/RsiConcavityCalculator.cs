namespace Drps.Calculator.Rsi;

/// <summary>
/// Pure, deterministic second discrete difference - RsiConcavity[t] = RsiSlope[t] -
/// RsiSlope[t-1]. No DB/EF dependency, directly unit-testable. Detects a genuine inflection
/// point in RSI's own rate of change, distinct from what RsiSlope's zero-cross already finds
/// (RSI's local peaks/troughs) - see CLAUDE.md's "RsiSlope / RsiConcavity: Design Direction
/// Locked" (2026-07-31).
///
/// Operates on an already-computed RsiSlope series (NOT RSI directly, and NOT raw bars) - the
/// step back is always exactly 1 slope reading, unlike RsiSlopeCalculator's configurable
/// Lookback, since concavity is defined as the difference between two IMMEDIATELY consecutive
/// slope values, not a wider comparison window.
/// </summary>
public static class RsiConcavityCalculator
{
    public readonly record struct RsiConcavityInput(DateOnly Date, decimal SlopeValue);

    public readonly record struct RsiConcavityResult(DateOnly Date, decimal Value);

    /// <summary>
    /// Single forward pass over <paramref name="slopeSeries"/> (must already be ordered by date
    /// ascending, one entry per already-computed RsiSlope row). A value is only emitted starting
    /// at index 1 (the first index with an immediate predecessor slope reading available).
    /// </summary>
    public static IReadOnlyList<RsiConcavityResult> Compute(IReadOnlyList<RsiConcavityInput> slopeSeries)
    {
        var results = new List<RsiConcavityResult>();

        if (slopeSeries.Count < 2)
        {
            return results;
        }

        for (var i = 1; i < slopeSeries.Count; i++)
        {
            var value = slopeSeries[i].SlopeValue - slopeSeries[i - 1].SlopeValue;

            // Rounded to the same decimal(18,4) precision RsiConcavityIndicator.Value is stored
            // at - both operands are already-rounded decimal(18,4) slope values, same reasoning
            // as RsiSlopeCalculator's own rounding comment.
            results.Add(new RsiConcavityResult(slopeSeries[i].Date, Math.Round(value, 4, MidpointRounding.AwayFromZero)));
        }

        return results;
    }
}
