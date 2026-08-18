using Drps.Shared.Models;

namespace Drps.Gate.Scoring;

/// <summary>
/// Sanity-checks a single GateParameters row's internal consistency before GateScanService
/// ever constructs GateQualityScorer/GateCompositeService from it. Closes the latent gap
/// CLAUDE.md flags: the migration's SQL-level column defaults for every threshold column are
/// 0/0m, while GateParametersSeeder always sets real values explicitly - today the seeder is
/// the only insert path, so an all-zero row is latent, not active, but GateScanService had no
/// check that an "active" row's values are actually sane, only that exactly one exists.
///
/// Deliberately scoped to one row's own internal consistency, not a cross-row check (e.g.
/// not "is this row's EffectiveFrom sane relative to other rows") - see this task's own scope
/// boundary.
/// </summary>
public static class GateParametersValidator
{
    public static IReadOnlyList<string> Validate(GateParameters parameters)
    {
        var violations = new List<string>();

        if (!(parameters.RsiLowerBound < parameters.RsiPeak && parameters.RsiPeak < parameters.RsiUpperBound))
        {
            violations.Add(
                $"RsiLowerBound < RsiPeak < RsiUpperBound must hold (got {parameters.RsiLowerBound}, " +
                $"{parameters.RsiPeak}, {parameters.RsiUpperBound})");
        }

        // Exclusive of 0 only, per this task's own framing - a 0 floor would mean "always
        // pass," defeating the Tier 1 hard-reject gate entirely. A floor of exactly 1.0 is a
        // legitimate (if maximally strict) configuration, not a defeated gate.
        if (!(parameters.RsiFloorQuality > 0m && parameters.RsiFloorQuality <= 1m))
        {
            violations.Add($"RsiFloorQuality must be > 0 and <= 1 (got {parameters.RsiFloorQuality})");
        }

        // RsiPeak > 0 closes the gap where RsiLowerBound < RsiPeak alone wouldn't catch a
        // degenerate RsiPeak=0 (or negative) if RsiLowerBound were also negative - a 0 or
        // negative RSI peak makes no sense against RSI's real [0,100] domain.
        if (!(parameters.RsiPeak > 0m))
        {
            violations.Add($"RsiPeak must be > 0 (got {parameters.RsiPeak})");
        }

        if (!(parameters.RvolFloorMultiple < parameters.RvolCeilingMultiple))
        {
            violations.Add(
                $"RvolFloorMultiple < RvolCeilingMultiple must hold (got {parameters.RvolFloorMultiple}, " +
                $"{parameters.RvolCeilingMultiple})");
        }

        if (!(parameters.RvolCeilingMultiple > 0m))
        {
            violations.Add($"RvolCeilingMultiple must be > 0 (got {parameters.RvolCeilingMultiple})");
        }

        if (!(parameters.RvolFullWeight > 0m && parameters.RvolHalfWeight > 0m))
        {
            violations.Add(
                $"RvolFullWeight and RvolHalfWeight must both be > 0 (got {parameters.RvolFullWeight}, " +
                $"{parameters.RvolHalfWeight})");
        }

        // Half-weight must actually be less than full weight, or the "unverified RVOL counts
        // at half weight" rule (Gate: First Design Decision) is inverted or a no-op.
        if (!(parameters.RvolHalfWeight < parameters.RvolFullWeight))
        {
            violations.Add(
                $"RvolHalfWeight must be less than RvolFullWeight (got {parameters.RvolHalfWeight} >= " +
                $"{parameters.RvolFullWeight})");
        }

        // Both bounds exclusive: 0 would zero out RSI's contribution entirely (RSI is
        // described elsewhere as the actual strategy edge, never meant to count for nothing),
        // 1 would zero out RVOL's contribution entirely (RVOL's whole role is confirmation of
        // an RSI-suggested move - it isn't meant to be silently dropped either).
        if (!(parameters.RsiCompositeWeight > 0m && parameters.RsiCompositeWeight < 1m))
        {
            violations.Add($"RsiCompositeWeight must be strictly between 0 and 1 (got {parameters.RsiCompositeWeight})");
        }

        if (!(parameters.ExitThreshold < parameters.WatchThreshold && parameters.WatchThreshold < parameters.BuyThreshold))
        {
            violations.Add(
                $"ExitThreshold < WatchThreshold < BuyThreshold must hold (got {parameters.ExitThreshold}, " +
                $"{parameters.WatchThreshold}, {parameters.BuyThreshold})");
        }

        // ExitThreshold > 0 alone, combined with the strict ordering check above, transitively
        // guarantees WatchThreshold and BuyThreshold are also > 0 - closes the "0 where
        // nonsensical" gap for all three bucket thresholds with one check. A composite score
        // is always >= 0 by construction (both quality curves floor at 0.0), so a
        // zero-or-negative ExitThreshold would mean EXIT could never realistically trigger.
        if (!(parameters.ExitThreshold > 0m))
        {
            violations.Add($"ExitThreshold must be > 0 (got {parameters.ExitThreshold})");
        }

        if (!(parameters.NoBuySessionCount > 0))
        {
            violations.Add($"NoBuySessionCount must be > 0 (got {parameters.NoBuySessionCount})");
        }

        return violations;
    }

    public static bool IsValid(GateParameters parameters) => Validate(parameters).Count == 0;
}
