namespace Drps.Calculator.Rsi;

/// <summary>
/// Pure, deterministic 14-period RSI (Relative Strength Index), computed from Close-to-
/// Close price changes using Wilder's original smoothing method (a modified moving average
/// with alpha = 1/Period) - explicitly NOT a simple-moving-average variant of average
/// gain/loss. This is the standard, most widely published RSI formula (J. Welles Wilder,
/// "New Concepts in Technical Trading Systems", 1978). No DB/EF dependency, so it's
/// directly unit-testable. Anti-Spaghetti Rule #2: math lives in deterministic C#, not an
/// LLM.
/// </summary>
public static class RsiCalculator
{
    public const int Period = 14;

    public readonly record struct RsiBarInput(DateOnly Date, decimal Close);

    public readonly record struct RsiResult(DateOnly Date, int Period, decimal Value);

    /// <summary>
    /// Single forward pass over <paramref name="bars"/> (must already be ordered by date
    /// ascending). Wilder's initial average gain/loss is a simple average of the first
    /// Period price changes, which requires Period + 1 closes (14 deltas need 15 closes) -
    /// no value is emitted before that. Every subsequent value re-smooths the running
    /// average using only the latest bar's gain/loss:
    /// avg = (prevAvg * (Period - 1) + current) / Period - an exponentially-weighted
    /// running average, not a fixed rolling recompute like DmaCalculator's simple moving
    /// average. This means Wilder's RSI technically has an unbounded dependency chain back
    /// to the series' first bar (each value depends on all prior values, with exponentially
    /// decaying weight) - see RsiGapChecker/RsiExDividendAnnotator for how this codebase
    /// deliberately bounds that to a fixed trailing window for gap/ex-div/verification
    /// purposes, consistent with DMA's fixed-window discipline.
    /// </summary>
    public static IReadOnlyList<RsiResult> Compute(IReadOnlyList<RsiBarInput> bars)
    {
        var results = new List<RsiResult>();

        if (bars.Count < Period + 1)
        {
            return results;
        }

        decimal gainSum = 0m;
        decimal lossSum = 0m;
        for (var i = 1; i <= Period; i++)
        {
            var delta = bars[i].Close - bars[i - 1].Close;
            gainSum += Math.Max(delta, 0m);
            lossSum += Math.Max(-delta, 0m);
        }

        var avgGain = gainSum / Period;
        var avgLoss = lossSum / Period;

        results.Add(new RsiResult(bars[Period].Date, Period, ComputeRsi(avgGain, avgLoss)));

        for (var i = Period + 1; i < bars.Count; i++)
        {
            var delta = bars[i].Close - bars[i - 1].Close;
            var gain = Math.Max(delta, 0m);
            var loss = Math.Max(-delta, 0m);

            avgGain = (avgGain * (Period - 1) + gain) / Period;
            avgLoss = (avgLoss * (Period - 1) + loss) / Period;

            results.Add(new RsiResult(bars[i].Date, Period, ComputeRsi(avgGain, avgLoss)));
        }

        return results;
    }

    private static decimal ComputeRsi(decimal avgGain, decimal avgLoss)
    {
        // No losses at all over the smoothed window - maximally overbought, and avoids a
        // divide-by-zero in the RS ratio below.
        if (avgLoss == 0m)
        {
            return 100m;
        }

        var rs = avgGain / avgLoss;
        var rsi = 100m - (100m / (1m + rs));

        // Rounded to the same decimal(18,4) precision RsiIndicator.Value is stored at (Data
        // Type Discipline). avgGain/avgLoss themselves are NOT rounded - the Wilder-smoothing
        // recurrence in Compute() carries full decimal precision forward between bars; only
        // the reported/stored value is rounded here. Without this, two independently-rounded
        // repeating-decimal divisions (avgGain/Period, avgLoss/Period) don't exactly cancel
        // back to their true ratio in finite-precision decimal arithmetic - e.g. a mathematically
        // exact RSI of 75 can otherwise compute as 74.999999999999999999999999999.
        return Math.Round(rsi, 4, MidpointRounding.AwayFromZero);
    }
}
