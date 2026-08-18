namespace Drps.Calculator.Rsi;

/// <summary>
/// Pure, deterministic first discrete difference of an already-computed RSI series -
/// RsiSlope[t] = RSI[t] - RSI[t-Lookback]. No DB/EF dependency, directly unit-testable.
/// Anti-Spaghetti Rule #2: math lives in deterministic C#, not an LLM.
///
/// Per CLAUDE.md's "RsiSlope / RsiConcavity: Design Direction Locked" (2026-07-31), Lookback
/// is a config-driven value (CalculatorSettings.RsiSlopeLookback), not a hardcoded constant -
/// unlike RsiCalculator.Period/RvolCalculator.BaselineWindow, which are fixed literals, this
/// calculator takes Lookback as a parameter so a future config change needs no code change.
///
/// Operates on <paramref name="rsiSeries"/> - already-computed RSI (date, value) pairs, NOT raw
/// bars. This is a genuinely different input shape than every other Calculator indicator
/// (DMA/RSI/RVOL/ATR), which all compute from RawOhlcvBar - RsiSlope/RsiConcavity are the first
/// indicators in this codebase computed from another indicator's own output.
/// </summary>
public static class RsiSlopeCalculator
{
    public readonly record struct RsiSlopeInput(DateOnly Date, decimal RsiValue);

    public readonly record struct RsiSlopeResult(DateOnly Date, int Lookback, decimal Value);

    /// <summary>
    /// Single forward pass over <paramref name="rsiSeries"/> (must already be ordered by date
    /// ascending, one entry per already-computed RSI row). A value is only emitted starting at
    /// index Lookback (the first index with a full Lookback-back predecessor available).
    /// </summary>
    public static IReadOnlyList<RsiSlopeResult> Compute(IReadOnlyList<RsiSlopeInput> rsiSeries, int lookback)
    {
        if (lookback <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lookback), lookback, "RSI slope lookback must be positive.");
        }

        var results = new List<RsiSlopeResult>();

        if (rsiSeries.Count <= lookback)
        {
            return results;
        }

        for (var i = lookback; i < rsiSeries.Count; i++)
        {
            var value = rsiSeries[i].RsiValue - rsiSeries[i - lookback].RsiValue;

            // Rounded to the same decimal(18,4) precision RsiSlopeIndicator.Value is stored at
            // (Data Type Discipline) - both operands are already rounded decimal(18,4) RSI
            // values (RsiCalculator.ComputeRsi), so this subtraction needs no intermediate
            // full-precision carry-forward the way RSI's own Wilder recurrence does.
            results.Add(new RsiSlopeResult(rsiSeries[i].Date, lookback, Math.Round(value, 4, MidpointRounding.AwayFromZero)));
        }

        return results;
    }
}
