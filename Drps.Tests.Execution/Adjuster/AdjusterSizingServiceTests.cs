using Drps.Adjuster.Sizing;
using Drps.Shared.Models;

namespace Drps.Tests.Adjuster;

// Every test in this class originally called ComputeAllocation, which internally calls
// AdjusterSizingService's tier/relative-strength/base-rate logic - redacted for public release
// (see AdjusterSizingService.cs). What survives is the reserve-schedule coverage, which calls
// the separate, non-redacted static ComputeReserveAdjustedAvailableCapital method directly (it
// delegates to Drps.Shared.Positioning.CapitalReserveCalculator, never touching the redacted
// tier logic at all).
public class AdjusterSizingServiceTests
{
    // [REDACTED FOR PUBLIC RELEASE] Placeholder fixture values, not DRPS's real shipped
    // tuning - see README.md's "What's intentionally not public" section.
    private static readonly AdjusterParameters TestParameters = new()
    {
        TierOneFloor = 0.9m,
        TierOneCeiling = 0.93m,
        TierTwoCeiling = 0.96m,
        TierOneBaseRate = 0.02m,
        TierTwoBaseRate = 0.03m,
        TierThreeBaseRate = 0.04m,
        SectorCapPercent = 0.25m,
        BaseReservePercent = 0.2m,
        ReserveStepPercent = 0.05m,
        ReserveMilestoneOne = 5000m,
        ReserveMilestoneTwo = 50000m
    };

    [Theory]
    [InlineData(4000, 3200)] // below ReserveMilestoneOne: 20% reserve -> 80% of 4000
    [InlineData(5000, 3750)] // exactly ReserveMilestoneOne: 25% reserve -> 75% of 5000
    [InlineData(25000, 18750)] // between milestones: still 25% reserve -> 75% of 25000
    [InlineData(50000, 35000)] // exactly ReserveMilestoneTwo: 30% reserve -> 70% of 50000
    [InlineData(100000, 70000)] // above both milestones: still 30% reserve -> 70% of 100000
    public void ComputeReserveAdjustedAvailableCapital_SteppedAtBothMilestones_MatchesExpectedValue(
        decimal totalCapital, decimal expectedAvailable)
    {
        var available = AdjusterSizingService.ComputeReserveAdjustedAvailableCapital(TestParameters, totalCapital);

        Assert.Equal(expectedAvailable, available);
    }
}
