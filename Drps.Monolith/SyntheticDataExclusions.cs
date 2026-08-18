namespace Drps.Monolith;

/// <summary>
/// Hand-seeded synthetic verification rows, flagged in CLAUDE.md's "Ledger Live Verification
/// Pass - Hand-Seeded Synthetic Data Flagged" block (2026-07-16) as permanently off-limits for
/// any real grading or calibration. GateScore.Id=2's field values were fabricated round numbers
/// chosen to force a BUY bucket for test purposes (not GateQualityScorer/GateCompositeService
/// output); AdjusterAllocation.Id=1 is a genuinely real, organic sizing computation, but sized
/// against that fabricated candidate; Position.Id=1/Id=2 trace back to that same fabricated
/// lineage via their required GateScoreId/AdjusterAllocationId FKs.
///
/// Hard filter, not just labeling: MonolithDataLoader excludes these rows at load time, so
/// nothing downstream (ReplayJoinService, GradingService, GradingReportBuilder) ever sees them
/// - matches the CLAUDE.md block's explicit warning that mixing synthetic rows into a real
/// track-record computation would silently corrupt it. Exhaustive as of that block - not
/// expected to grow; any future synthetic seeding should get its own new, explicitly-flagged
/// exclusion rather than being folded into this one retroactively.
/// </summary>
public static class SyntheticDataExclusions
{
    public static readonly IReadOnlySet<long> SyntheticGateScoreIds = new HashSet<long> { 2 };

    public static readonly IReadOnlySet<long> SyntheticAdjusterAllocationIds = new HashSet<long> { 1 };

    public static readonly IReadOnlySet<long> SyntheticPositionIds = new HashSet<long> { 1, 2 };

    public static bool IsSyntheticGateScore(long gateScoreId) => SyntheticGateScoreIds.Contains(gateScoreId);

    public static bool IsSyntheticAdjusterAllocation(long adjusterAllocationId) =>
        SyntheticAdjusterAllocationIds.Contains(adjusterAllocationId);

    public static bool IsSyntheticPosition(long positionId) => SyntheticPositionIds.Contains(positionId);
}
