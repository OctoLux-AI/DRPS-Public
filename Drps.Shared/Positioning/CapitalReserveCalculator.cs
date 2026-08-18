using Drps.Shared.Models;

namespace Drps.Shared.Positioning;

// Extracted from Drps.Adjuster.Sizing.AdjusterSizingService so Drps.Execution's cash-floor
// pre-fire gate (CLAUDE.md's Execution Layer: First Design Decision) uses the identical
// reserve-percent formula rather than a second, potentially-drifting copy - same
// centralization precedent as TradingDayCalendar/FeederRetryPolicy elsewhere in this codebase.
// Lives in Drps.Shared (not Drps.Adjuster) specifically so Drps.Execution can read it without
// taking a project reference on Drps.Adjuster's own scoring/sizing layer - only on the
// AdjusterParameters model, which already lives here.
public static class CapitalReserveCalculator
{
    // Reserve percentage steps up by ReserveStepPercent at each of ReserveMilestoneOne/Two,
    // based on total capital's current level (Adjuster: Capital Reserve Step Schedule) - both
    // milestones can apply at once (e.g. 45% once total capital clears ReserveMilestoneTwo,
    // which is BaseReservePercent + 2 x ReserveStepPercent).
    public static decimal ComputeReserveAdjustedAvailableCapital(AdjusterParameters parameters, decimal totalCapital)
    {
        var reservePercent = parameters.BaseReservePercent;

        if (totalCapital >= parameters.ReserveMilestoneOne)
        {
            reservePercent += parameters.ReserveStepPercent;
        }

        if (totalCapital >= parameters.ReserveMilestoneTwo)
        {
            reservePercent += parameters.ReserveStepPercent;
        }

        return totalCapital * (1m - reservePercent);
    }
}
