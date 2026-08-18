namespace Drps.Calculator.Dma;

/// <summary>
/// Pure, deterministic DMA (simple moving average) computation - no DB/EF dependency, so it's
/// directly unit-testable. Anti-Spaghetti Rule #2: math lives in deterministic C#, not an LLM.
/// </summary>
public static class DmaCalculator
{
    public static readonly int[] Windows = { 5, 15, 30, 60 };

    public readonly record struct DmaBarInput(DateOnly Date, decimal Close);

    public readonly record struct DmaResult(DateOnly Date, int Window, decimal Value);

    /// <summary>
    /// Single forward pass over <paramref name="bars"/> (must already be ordered by date
    /// ascending), maintaining one independent rolling sum per window. A DMA-N value is only
    /// emitted for a bar once at least N bars (including itself) have been seen for that
    /// symbol - no shorter-window backfill for the leading bars of a series.
    /// </summary>
    public static IReadOnlyList<DmaResult> Compute(IReadOnlyList<DmaBarInput> bars)
    {
        var results = new List<DmaResult>();
        var rollingSums = new decimal[Windows.Length];

        for (var i = 0; i < bars.Count; i++)
        {
            for (var w = 0; w < Windows.Length; w++)
            {
                var window = Windows[w];

                rollingSums[w] += bars[i].Close;
                if (i >= window)
                {
                    rollingSums[w] -= bars[i - window].Close;
                }

                if (i >= window - 1)
                {
                    results.Add(new DmaResult(bars[i].Date, window, rollingSums[w] / window));
                }
            }
        }

        return results;
    }
}
