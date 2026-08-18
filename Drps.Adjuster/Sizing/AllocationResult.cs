namespace Drps.Adjuster.Sizing;

/// <summary>
/// AdjusterSizingService's output for one BUY-bucket candidate. Tier/RelativeStrengthMultiplier
/// are always populated (computed before the sector-cap check) even when Funded is false, so a
/// caller can see how strong the candidate's raw sizing math was independent of whether capital
/// was actually available for it. When Funded is false, AllocationPercent/AllocationDollarAmount/
/// ShareCount are all 0 and ShareCapDeficient is false - "skipped, no room" (Funded = false) is
/// kept deliberately distinct from "sized but rounds to zero shares" (Funded = true,
/// ShareCapDeficient = true), per the Adjuster: Sector Cap and Position-Sizing Formula decision's
/// fractional-share rule.
/// </summary>
public sealed record AllocationResult
{
    public required int Tier { get; init; }

    public required decimal RelativeStrengthMultiplier { get; init; }

    public required decimal AllocationPercent { get; init; }

    public required decimal AllocationDollarAmount { get; init; }

    // Now actually enforced (folded into Funded below) against
    // PortfolioState.TotalDeployedCapital plus the caller's own scan-local running total -
    // still exposed here too so a caller can see the raw ceiling itself, not just whether it
    // was cleared.
    public required decimal ReserveAdjustedAvailableCapital { get; init; }

    // decimal(18,9) - CLAUDE.md's Execution Layer: Third Correction. Previously `long`,
    // which forced ComputeAllocation to truncate its precisely-computed quantity to a whole
    // number before ever returning it.
    public required decimal ShareCount { get; init; }

    public required bool ShareCapDeficient { get; init; }

    // False means the sector-cap check (with single-attempt displacement) could not fit this
    // candidate - Adjuster: Eleventh Decision's "skip funding entirely, no partial fund, no
    // cascading" rule. True means capital was approved, regardless of whether it rounds down
    // to a whole share (see ShareCapDeficient).
    public required bool Funded { get; init; }

    // Insider Form 4 Adjuster Multiplier Decision (CLAUDE.md 2026-07-19) - the raw
    // insider-purchase multiplier for this candidate, already independently capped by the
    // caller (InsiderLookupService) to [1.0, InsiderLookupService.MultiplierCap]. Per
    // CLAUDE.md's Adjuster: Multi-Signal Multiplier Combination - Weighted-Deviation-Sum Model
    // (2026-07-26), this is no longer multiplied into AllocationPercent/AllocationDollarAmount
    // above - it is recorded here purely so OrderFiringService can combine it with the
    // sentiment multiplier (only known live, at fire time) via MultiSignalMultiplierCombiner.
    // Always populated (default 1.0, neutral) even when Funded is false, same "always visible
    // regardless of funding outcome" precedent as Tier/RelativeStrengthMultiplier.
    public required decimal InsiderMultiplierApplied { get; init; }

    // True only when InsiderMultiplierApplied is neutral because the underlying dollar-
    // volume data could not be computed at all - not true for a genuine "checked, zero
    // insider purchases" result. See InsiderMultiplierResult's own doc comment for why that
    // distinction matters to a human reviewing allocations.
    public required bool InsiderDataUnverified { get; init; }
}
