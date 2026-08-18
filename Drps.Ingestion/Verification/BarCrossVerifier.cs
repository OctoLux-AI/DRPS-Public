using Drps.Shared.Models;

namespace Drps.Ingestion.Verification;

// Kept separate from AlpacaFeeder/FinnhubClient/Worker on purpose — CLAUDE.md calls out
// CapitalFill's NewsIngestionService (fetch + parse + rules + dispatch in one class) as
// SRP tech debt to not repeat. This class only compares and merges bars already fetched
// by the source-specific clients; it does no fetching of its own.
public static class BarCrossVerifier
{
    // Per CLAUDE.md's raw-price tolerance rule: two legitimate sources reporting the
    // same closed bar should match almost exactly.
    private const decimal ToleranceFractionPct = 0.1m;

    public static IReadOnlyList<OhlcvBar> Merge(
        IReadOnlyList<OhlcvBar> primary,
        IReadOnlyList<OhlcvBar> secondary,
        ILogger logger)
    {
        var secondaryByKey = secondary.ToDictionary(b => (b.Ticker, b.BarDate));
        var matchedKeys = new HashSet<(string Ticker, DateOnly BarDate)>();
        var merged = new List<OhlcvBar>(primary.Count + secondary.Count);

        foreach (var bar in primary)
        {
            var key = (bar.Ticker, bar.BarDate);
            if (secondaryByKey.TryGetValue(key, out var match))
            {
                matchedKeys.Add(key);
                merged.Add(CompareAndMerge(bar, match, logger));
            }
            else
            {
                // Single-source bar — not a mismatch, just not cross-verified yet.
                merged.Add(bar);
            }
        }

        foreach (var bar in secondary)
        {
            var key = (bar.Ticker, bar.BarDate);
            if (!matchedKeys.Contains(key))
                merged.Add(bar);
        }

        return merged;
    }

    private static OhlcvBar CompareAndMerge(OhlcvBar primaryBar, OhlcvBar secondaryBar, ILogger logger)
    {
        var closeVariancePct = VariancePct(primaryBar.Close, secondaryBar.Close);
        var verified = closeVariancePct <= ToleranceFractionPct;

        // Open/High/Low/Volume never gate Verified — informational only, logged so a
        // real divergence is visible without silently treating the bars as identical.
        logger.LogInformation(
            "[VERIFY/INFO]: {Ticker} {BarDate} informational variance — Open={OpenVariancePct:F4}% High={HighVariancePct:F4}% Low={LowVariancePct:F4}% Volume={VolumeVariancePct:F4}%",
            primaryBar.Ticker, primaryBar.BarDate,
            VariancePct(primaryBar.Open, secondaryBar.Open),
            VariancePct(primaryBar.High, secondaryBar.High),
            VariancePct(primaryBar.Low, secondaryBar.Low),
            VariancePct(primaryBar.Volume, secondaryBar.Volume));

        if (!verified)
        {
            logger.LogWarning(
                "[VERIFY/MISMATCH]: {Ticker} {BarDate} Close variance {VariancePct:F4}% exceeds {Tolerance:F2}% tolerance — {PrimarySource}={PrimaryClose} vs {SecondarySource}={SecondaryClose}",
                primaryBar.Ticker, primaryBar.BarDate, closeVariancePct, ToleranceFractionPct,
                primaryBar.Source, primaryBar.Close, secondaryBar.Source, secondaryBar.Close);
        }

        return primaryBar with
        {
            Source = $"{primaryBar.Source}+{secondaryBar.Source}",
            SampleCount = 2,
            VariancePct = closeVariancePct,
            Verified = verified
        };
    }

    private static decimal VariancePct(decimal a, decimal b)
    {
        if (a == 0m)
            return b == 0m ? 0m : 100m;

        return Math.Abs(a - b) / a * 100m;
    }

    private static decimal VariancePct(long a, long b) => VariancePct((decimal)a, (decimal)b);
}
