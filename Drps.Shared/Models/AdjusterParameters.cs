using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Versioned, append-only table of Adjuster's tunable numeric thresholds - same shape and
/// purpose as GateParameters. Every AdjusterAllocation row tags which AdjusterParameters row
/// (IsActive at computation time) it was sized under, mirroring GateScore's own
/// GateParameterVersion discipline.
///
/// [REDACTED FOR PUBLIC RELEASE] The property defaults below are placeholders, not DRPS's
/// real shipped tuning - see README.md's "What's intentionally not public" section. They are
/// deliberately kept internally consistent (ordered tiers, TierOneFloor matching Gate's own
/// BuyThreshold placeholder, etc.) so every consumer that depends on this shape still compiles
/// and behaves sensibly; only the specific calibrated numbers are changed.
/// </summary>
public class AdjusterParameters
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public DateTime EffectiveFrom { get; set; }

    public bool IsActive { get; set; }

    // Position-sizing tier boundaries. TierOneFloor is inclusive - matches Gate's own
    // BuyThreshold, closing the boundary gap between Gate and Adjuster. TierTwoFloor is
    // implicitly TierOneCeiling and TierThreeFloor is implicitly TierTwoCeiling - no explicit
    // columns for those. Tier 3 is open-ended above TierTwoCeiling, so it has no ceiling
    // column either.
    public decimal TierOneFloor { get; set; } = 0.9m;

    public decimal TierOneCeiling { get; set; } = 0.93m;

    public decimal TierTwoCeiling { get; set; } = 0.96m;

    // Base allocation rate by tier - higher tiers get more capital.
    public decimal TierOneBaseRate { get; set; } = 0.02m;

    public decimal TierTwoBaseRate { get; set; } = 0.03m;

    public decimal TierThreeBaseRate { get; set; } = 0.04m;

    // Sector cap, measured against total capital, not deployed capital.
    public decimal SectorCapPercent { get; set; } = 0.25m;

    // Capital reserve step schedule - starts at BaseReservePercent, steps up by
    // ReserveStepPercent at each of the two milestones.
    public decimal BaseReservePercent { get; set; } = 0.2m;

    public decimal ReserveStepPercent { get; set; } = 0.05m;

    public decimal ReserveMilestoneOne { get; set; } = 5000m;

    public decimal ReserveMilestoneTwo { get; set; } = 50000m;

    // Concurrent-position cap - DRPS's hard limit on simultaneously-open Position rows,
    // enforced by PreFireGateService.EvaluateOpenAsync. Position-sizing tiers
    // (TierOne/Two/ThreeBaseRate above) are explicitly unaffected by this column - a
    // diversification/velocity change only, not a risk-per-trade change.
    public int MaxConcurrentPositions { get; set; } = 10;

    // Concurrent-position-cap displacement margin - a new BUY-bucket candidate must score at
    // least this much higher, relatively, than the account-wide weakest currently-held
    // position's composite score before that holding is displaced (newScore >= weakestHeldScore
    // x (1 + this value)) once the account is at MaxConcurrentPositions.
    public decimal ConcurrentPositionDisplacementMarginPercent { get; set; } = 0.05m;
}
