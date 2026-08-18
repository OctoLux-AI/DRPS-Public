using Drps.Shared.Models;

namespace Drps.Adjuster.Scoring;

/// <summary>
/// Sanity-checks a single AdjusterParameters row's internal consistency, same rigor and shape
/// as GateParametersValidator - closes the same latent gap: the migration's SQL-level column
/// defaults for every threshold column are 0/0m, and nothing yet checks that an "active" row's
/// values are actually sane, only that exactly one exists.
///
/// Deliberately scoped to one row's own internal consistency, not a cross-row check.
/// </summary>
public static class AdjusterParametersValidator
{
    public static IReadOnlyList<string> Validate(AdjusterParameters parameters)
    {
        var violations = new List<string>();

        if (!(parameters.TierOneFloor < parameters.TierOneCeiling && parameters.TierOneCeiling < parameters.TierTwoCeiling))
        {
            violations.Add(
                $"TierOneFloor < TierOneCeiling < TierTwoCeiling must hold (got {parameters.TierOneFloor}, " +
                $"{parameters.TierOneCeiling}, {parameters.TierTwoCeiling})");
        }

        if (!(parameters.TierOneBaseRate < parameters.TierTwoBaseRate && parameters.TierTwoBaseRate < parameters.TierThreeBaseRate))
        {
            violations.Add(
                $"TierOneBaseRate < TierTwoBaseRate < TierThreeBaseRate must hold (got {parameters.TierOneBaseRate}, " +
                $"{parameters.TierTwoBaseRate}, {parameters.TierThreeBaseRate})");
        }

        if (!(parameters.SectorCapPercent > 0m && parameters.SectorCapPercent < 1m))
        {
            violations.Add($"SectorCapPercent must be strictly between 0 and 1 (got {parameters.SectorCapPercent})");
        }

        if (!(parameters.BaseReservePercent > 0m && parameters.BaseReservePercent < 1m))
        {
            violations.Add($"BaseReservePercent must be strictly between 0 and 1 (got {parameters.BaseReservePercent})");
        }

        if (!(parameters.ReserveStepPercent > 0m))
        {
            violations.Add($"ReserveStepPercent must be > 0 (got {parameters.ReserveStepPercent})");
        }

        // Guards against the reserve schedule ever reaching or exceeding 100% at the second
        // milestone step, which would leave zero (or negative) capital deployable past it.
        if (!(parameters.BaseReservePercent + (2m * parameters.ReserveStepPercent) < 1m))
        {
            violations.Add(
                "BaseReservePercent + (2 * ReserveStepPercent) must be < 1 (got " +
                $"{parameters.BaseReservePercent} + (2 * {parameters.ReserveStepPercent}) = " +
                $"{parameters.BaseReservePercent + (2m * parameters.ReserveStepPercent)})");
        }

        if (!(parameters.ReserveMilestoneOne < parameters.ReserveMilestoneTwo))
        {
            violations.Add(
                $"ReserveMilestoneOne < ReserveMilestoneTwo must hold (got {parameters.ReserveMilestoneOne}, " +
                $"{parameters.ReserveMilestoneTwo})");
        }

        if (!(parameters.ReserveMilestoneOne > 0m))
        {
            violations.Add($"ReserveMilestoneOne must be > 0 (got {parameters.ReserveMilestoneOne})");
        }

        if (!(parameters.ReserveMilestoneTwo > 0m))
        {
            violations.Add($"ReserveMilestoneTwo must be > 0 (got {parameters.ReserveMilestoneTwo})");
        }

        if (!(parameters.ConcurrentPositionDisplacementMarginPercent >= 0m))
        {
            violations.Add(
                "ConcurrentPositionDisplacementMarginPercent must be >= 0 (got " +
                $"{parameters.ConcurrentPositionDisplacementMarginPercent})");
        }

        return violations;
    }

    public static bool IsValid(AdjusterParameters parameters) => Validate(parameters).Count == 0;
}
