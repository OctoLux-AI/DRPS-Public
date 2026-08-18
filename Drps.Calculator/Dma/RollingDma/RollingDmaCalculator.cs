namespace Drps.Calculator.Dma.RollingDma;

/// <summary>
/// Pure, deterministic incremental DMA rolling-sum math - no DB/EF dependency, directly
/// unit-testable. Anti-Spaghetti Rule #2: math lives in deterministic C#, not an LLM.
///
/// This is the O(1)-per-term update CLAUDE.md's "Rolling DMA State Machine" section describes:
/// given the prior night's rolling sum/bar count for one (Ticker, Term), fold in today's new
/// close and (once the window is already full) drop exactly the one bar falling out of it -
/// never a full recompute over the whole trailing window. The bounded from-scratch recompute
/// used for bootstrap/re-anchor/gap-recovery lives in <see cref="ComputeSumFromScratch"/>,
/// still O(w) (w = Term, a fixed constant - never proportional to a ticker's total history),
/// deliberately kept in this same pure class since both are the same underlying arithmetic.
/// </summary>
public static class RollingDmaCalculator
{
    public readonly record struct UpdateResult(decimal RollingSum, int BarCount, decimal? Value);

    /// <summary>
    /// Folds one new bar's close into an existing (Ticker, Term) rolling-sum state.
    /// <paramref name="droppedClose"/> is the close of the single bar now falling out of the
    /// trailing window - required (and used) only once <paramref name="priorBarCount"/> has
    /// already reached <paramref name="term"/>; while the window is still filling, no bar is
    /// dropped and the sum simply accumulates. <see cref="UpdateResult.Value"/> is null
    /// (insufficient history) until the returned BarCount reaches <paramref name="term"/>.
    /// </summary>
    public static UpdateResult ApplyIncrementalUpdate(
        decimal priorRollingSum, int priorBarCount, int term, decimal newClose, decimal? droppedClose)
    {
        decimal newSum;
        int newBarCount;

        if (priorBarCount < term)
        {
            // Window not yet full - nothing to drop yet, just accumulate.
            newSum = priorRollingSum + newClose;
            newBarCount = priorBarCount + 1;
        }
        else
        {
            if (droppedClose is null)
            {
                throw new ArgumentNullException(
                    nameof(droppedClose),
                    $"Window is already full (BarCount={priorBarCount} >= Term={term}) - a dropped bar close is required to advance it.");
            }

            newSum = priorRollingSum - droppedClose.Value + newClose;
            newBarCount = term; // pinned at Term forever once the window is full
        }

        var value = newBarCount >= term ? newSum / term : (decimal?)null;
        return new UpdateResult(newSum, newBarCount, value);
    }

    /// <summary>
    /// Bounded O(w) from-scratch recompute - used for bootstrap (brand new ticker, or one
    /// re-entering the universe after an absence), a detected trading-calendar gap (incremental
    /// math can no longer be trusted across a missing expected trading day), and the fixed
    /// 30-night re-anchor cadence (periodic drift correction against accumulated
    /// incremental-rounding error). <paramref name="orderedCloses"/> must already be ordered
    /// ascending by date and need not be pre-trimmed to <paramref name="term"/> bars - only the
    /// trailing <paramref name="term"/> closes are summed. Returns BarCount &lt; term (and a
    /// null Value) when fewer than <paramref name="term"/> closes are available at all.
    /// </summary>
    public static UpdateResult ComputeSumFromScratch(IReadOnlyList<decimal> orderedCloses, int term)
    {
        var window = orderedCloses.Count >= term
            ? orderedCloses.Skip(orderedCloses.Count - term).ToList()
            : orderedCloses;

        var sum = window.Sum();
        var barCount = window.Count;
        var value = barCount >= term ? sum / term : (decimal?)null;

        return new UpdateResult(sum, barCount, value);
    }
}
