namespace Drps.Calculator.Atr;

/// <summary>
/// Pure, deterministic 14-period ATR (Average True Range), computed from True Range using
/// Wilder's original smoothing method (a modified moving average with alpha = 1/Period) -
/// explicitly NOT a plain simple-moving-average of range. This is the standard, most
/// widely published ATR formula (J. Welles Wilder, "New Concepts in Technical Trading
/// Systems", 1978) - the same smoothing method already used for RsiCalculator. No DB/EF
/// dependency, so it's directly unit-testable. Anti-Spaghetti Rule #2: math lives in
/// deterministic C#, not an LLM.
/// </summary>
public static class AtrCalculator
{
    public const int Period = 14;

    public readonly record struct AtrBarInput(DateOnly Date, decimal High, decimal Low, decimal Close);

    public readonly record struct AtrResult(DateOnly Date, int Period, decimal Value);

    /// <summary>
    /// Single forward pass over <paramref name="bars"/> (must already be ordered by date
    /// ascending). True Range for bar i is max(High[i] - Low[i], |High[i] - Close[i-1]|,
    /// |Low[i] - Close[i-1]|) - it depends on the PREVIOUS bar's close, same as RSI's
    /// delta. Wilder's initial ATR is a simple average of the first Period True Range
    /// values, which requires Period + 1 closes (14 true ranges need 15 bars) - no value is
    /// emitted before that. Every subsequent value re-smooths the running average using only
    /// the latest bar's true range: atr = (prevAtr * (Period - 1) + currentTrueRange) /
    /// Period - an exponentially-weighted running average, not a fixed rolling recompute.
    /// Same unbounded-true-dependency-chain caveat as RsiCalculator - see
    /// AtrGapChecker/AtrExDividendAnnotator for how this codebase deliberately bounds that
    /// to a fixed trailing window.
    /// </summary>
    public static IReadOnlyList<AtrResult> Compute(IReadOnlyList<AtrBarInput> bars)
    {
        var results = new List<AtrResult>();

        if (bars.Count < Period + 1)
        {
            return results;
        }

        decimal trueRangeSum = 0m;
        for (var i = 1; i <= Period; i++)
        {
            trueRangeSum += TrueRange(bars[i], bars[i - 1]);
        }

        var atr = trueRangeSum / Period;
        results.Add(new AtrResult(bars[Period].Date, Period, Round(atr)));

        for (var i = Period + 1; i < bars.Count; i++)
        {
            var trueRange = TrueRange(bars[i], bars[i - 1]);
            atr = (atr * (Period - 1) + trueRange) / Period;
            results.Add(new AtrResult(bars[i].Date, Period, Round(atr)));
        }

        return results;
    }

    private static decimal TrueRange(AtrBarInput current, AtrBarInput previous)
    {
        var highLow = current.High - current.Low;
        var highPrevClose = Math.Abs(current.High - previous.Close);
        var lowPrevClose = Math.Abs(current.Low - previous.Close);

        return Math.Max(highLow, Math.Max(highPrevClose, lowPrevClose));
    }

    // Rounded to the same decimal(18,4) precision AtrIndicator.Value is stored at (Data
    // Type Discipline). The running `atr` variable itself is NOT rounded between
    // iterations - only the reported/stored value is, same precedent as
    // RsiCalculator.ComputeRsi (avoids finite-precision-division artifacts accumulating
    // through the recurrence while still storing a clean, testable value).
    private static decimal Round(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
