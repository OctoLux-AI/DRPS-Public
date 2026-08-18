using Drps.Shared.Models;
using Drps.Shared.Positioning;

namespace Drps.Adjuster.Sizing;

/// <summary>
/// Pure sizing math for one BUY-bucket GateScore candidate (Adjuster: Fractional-Kelly-
/// Inspired Sizing, Sector Cap and Position-Sizing Formula, Eleventh Decision). No database
/// access, no async, no internal DateTime.Now call - every input (parameters, score, sector,
/// portfolio state, capital, price) is supplied by the caller. Orchestration (discovering
/// which GateScore rows need sizing, persisting the result as an AdjusterAllocation row) is
/// AdjusterScanService's job, a separate future task.
///
/// Produces the Kelly-sized base only (tier base rate x relative-strength multiplier) - per
/// CLAUDE.md's Adjuster: Multi-Signal Multiplier Combination - Weighted-Deviation-Sum Model
/// (2026-07-26), insider and sentiment no longer multiply directly against this base. They are
/// combined together (via MultiSignalMultiplierCombiner) and applied on top of this method's
/// output later, at fire time (OrderFiringService.FireAsync) - see ComputeAllocation's own
/// insiderMultiplier parameter comment for why.
/// </summary>
public class AdjusterSizingService
{
    public AllocationResult ComputeAllocation(
        AdjusterParameters parameters,
        decimal compositeScore,
        string? sector,
        PortfolioState portfolioState,
        decimal totalAvailableCapital,
        decimal pricePerShare,
        decimal? weakestHoldingComposite = null,
        decimal? weakestHoldingDollarAmount = null,
        // Scan-local running totals - dollars already committed to earlier candidates within
        // the SAME AdjusterScanService.RunScanAsync call, tracked by the caller only (never
        // persisted, never read from Ledger). Default 0m for any caller sizing a single
        // candidate in isolation (e.g. every existing unit test below) - a genuine no-op in
        // that case, not a behavior change.
        decimal runningTotalAllocatedThisScan = 0m,
        decimal runningSectorAllocatedThisScan = 0m,
        // Insider Form 4 Adjuster Multiplier Decision (CLAUDE.md 2026-07-19) - already
        // computed and independently capped by the caller (InsiderLookupService.GetMultiplierAsync,
        // always in [1.0, InsiderLookupService.MultiplierCap]) before it ever reaches this
        // method. This class never re-derives, re-clamps, or looks up insider data itself -
        // same "every input supplied by the caller, no DB access" discipline this class's own
        // doc comment already establishes for sector/portfolioState/price. Default 1.0m
        // (neutral) so every existing caller/test that doesn't pass this is unaffected.
        //
        // CLAUDE.md's Adjuster: Multi-Signal Multiplier Combination - Weighted-Deviation-Sum
        // Model (2026-07-26) - insiderMultiplier is deliberately NOT multiplied into
        // candidatePercent below anymore. It arrives at scan time (this method's own call
        // site); the sentiment multiplier it must combine with is only known live, at fire
        // time (OrderFiringService), so the only point both signals are ever known
        // simultaneously is fire time, not here. This method still records insiderMultiplier/
        // insiderDataUnverified on the result unchanged, purely so OrderFiringService's
        // MultiSignalMultiplierCombiner call has the raw value to combine later - "Kelly-sized
        // base" below is now genuinely Kelly-only, with no signal baked in.
        decimal insiderMultiplier = 1.0m,
        bool insiderDataUnverified = false)
    {
        var tier = DetermineTier(parameters, compositeScore);
        var multiplier = ComputeRelativeStrengthMultiplier(parameters, compositeScore, tier);
        var baseRate = GetTierBaseRate(parameters, tier);

        // Kelly-only base - see this method's own doc comment above for why insiderMultiplier
        // is recorded on the result (below) but no longer multiplied in here.
        var candidatePercent = baseRate * multiplier;
        var candidateDollarAmount = candidatePercent * totalAvailableCapital;

        var reserveAdjustedAvailableCapital = ComputeReserveAdjustedAvailableCapital(parameters, totalAvailableCapital);

        var fitsSectorCap = FitsWithinSectorCap(
            parameters, sector, portfolioState, totalAvailableCapital, candidateDollarAmount,
            runningSectorAllocatedThisScan, weakestHoldingDollarAmount);

        var fitsReserveCapital = FitsWithinReserveCapital(
            portfolioState, reserveAdjustedAvailableCapital, runningTotalAllocatedThisScan, candidateDollarAmount);

        var funded = fitsSectorCap && fitsReserveCapital;

        if (!funded)
        {
            return new AllocationResult
            {
                Tier = tier,
                RelativeStrengthMultiplier = multiplier,
                AllocationPercent = 0m,
                AllocationDollarAmount = 0m,
                ReserveAdjustedAvailableCapital = reserveAdjustedAvailableCapital,
                ShareCount = 0m,
                ShareCapDeficient = false,
                Funded = false,
                InsiderMultiplierApplied = insiderMultiplier,
                InsiderDataUnverified = insiderDataUnverified
            };
        }

        // No truncation - CLAUDE.md's Execution Layer: Third Correction. The precise
        // fractional quantity is exactly what OrderFiringService's two firing branches need
        // (floor-and-IOC for >=1 share, fire-the-fraction-as-Day-TIF for <1 share); discarding
        // that precision here, before either branch ever sees it, was the bug.
        var shareCount = candidateDollarAmount / pricePerShare;

        return new AllocationResult
        {
            Tier = tier,
            RelativeStrengthMultiplier = multiplier,
            AllocationPercent = candidatePercent,
            AllocationDollarAmount = candidateDollarAmount,
            ReserveAdjustedAvailableCapital = reserveAdjustedAvailableCapital,
            ShareCount = shareCount,
            // Zero-check against decimal 0m - still correct after dropping the Math.Floor
            // truncation: a candidate whose full (now-fractional) quantity is genuinely 0
            // only happens if candidateDollarAmount itself is 0, not from rounding a small
            // fraction down anymore (that fraction is exactly what gets fired now, via
            // OrderFiringService's sub-1-share branch, not discarded as deficient).
            ShareCapDeficient = shareCount == 0m,
            Funded = true,
            InsiderMultiplierApplied = insiderMultiplier,
            InsiderDataUnverified = insiderDataUnverified
        };
    }

    // [REDACTED FOR PUBLIC RELEASE]
    // Maps a composite score to a position-sizing tier (1/2/3), a relative-strength multiplier
    // within that tier's range (fractional-Kelly-inspired), and that tier's base allocation
    // rate. The exact tier boundaries, multiplier curve, and base rates are proprietary and not
    // included in this public repository - see README.md's "What's intentionally not public"
    // section. Signatures are left intact so ComputeAllocation's surrounding orchestration
    // (funding checks, sector-cap/reserve-capital enforcement, share-count math) stays visible
    // and buildable.
    private static int DetermineTier(AdjusterParameters parameters, decimal compositeScore)
    {
        throw new NotImplementedException("Redacted for public release - see README.md");
    }

    private static decimal ComputeRelativeStrengthMultiplier(AdjusterParameters parameters, decimal compositeScore, int tier)
    {
        throw new NotImplementedException("Redacted for public release - see README.md");
    }

    private static decimal GetTierBaseRate(AdjusterParameters parameters, int tier)
    {
        throw new NotImplementedException("Redacted for public release - see README.md");
    }

    // Delegates to Drps.Shared.Positioning.CapitalReserveCalculator (extracted so
    // Drps.Execution's cash-floor pre-fire gate uses the identical formula - see that class's
    // own doc comment). Kept as a public static member here, same signature as before the
    // extraction, so this class's existing callers/tests are unaffected.
    public static decimal ComputeReserveAdjustedAvailableCapital(AdjusterParameters parameters, decimal totalCapital) =>
        CapitalReserveCalculator.ComputeReserveAdjustedAvailableCapital(parameters, totalCapital);

    // SectorCapPercent x total capital = the sector's dollar ceiling (measured against total
    // capital, not deployed capital, per Adjuster: Sector Cap and Position-Sizing Formula).
    // Single-attempt displacement only (Adjuster: Eleventh Decision) - if the sector's single
    // weakest-composite holding (as reported by the caller, standing in for Drps.Ledger) would
    // free enough room for the candidate's FULL allocation, fund it; otherwise skip entirely -
    // no partial fund, no cascading to a second displacement.
    private static bool FitsWithinSectorCap(
        AdjusterParameters parameters,
        string? sector,
        PortfolioState portfolioState,
        decimal totalCapital,
        decimal candidateDollarAmount,
        decimal runningSectorAllocatedThisScan,
        decimal? weakestHoldingDollarAmount)
    {
        // A null sector means "cannot verify sector" (GateScore.Sector's own doc comment) -
        // the sector cap cannot be enforced against an unknown sector, so the candidate is
        // excluded from cap enforcement while remaining individually fundable, per that same
        // comment's explicit instruction.
        if (sector is null)
        {
            return true;
        }

        var sectorCeiling = parameters.SectorCapPercent * totalCapital;
        var baselineSectorDeployed = portfolioState.DeployedCapitalBySector.TryGetValue(sector, out var deployed)
            ? deployed
            : 0m;

        // Folded in, not a separate check - capital this same scan already committed to an
        // earlier candidate in this sector is just as real a claim on the sector's headroom
        // as anything PortfolioState itself reports.
        var currentSectorDeployed = baselineSectorDeployed + runningSectorAllocatedThisScan;

        if (currentSectorDeployed + candidateDollarAmount <= sectorCeiling)
        {
            return true;
        }

        // Null means no displacement candidate available (StubPortfolioStateProvider has no
        // real holdings today, so this always fails until Drps.Ledger exists) - treated as
        // "displacement not possible," not a fallback to a smaller allocation.
        if (!weakestHoldingDollarAmount.HasValue)
        {
            return false;
        }

        var projectedAfterDisplacement = currentSectorDeployed - weakestHoldingDollarAmount.Value + candidateDollarAmount;
        return projectedAfterDisplacement <= sectorCeiling;
    }

    // Portfolio-wide (not sector-scoped) counterpart to FitsWithinSectorCap - closes the gap
    // where ReserveAdjustedAvailableCapital was computed but never actually enforced anywhere.
    // Folds in the same scan-local running total (capital already committed to earlier
    // candidates in THIS scan, on top of whatever PortfolioState.TotalDeployedCapital already
    // reports as truly deployed). Deliberately no displacement attempt on a breach here, unlike
    // the sector-cap check above - today weakestHoldingDollarAmount is always null (no Ledger
    // yet) so this is a no-op distinction in practice; extending displacement to a
    // portfolio-wide ceiling as well as a sector-scoped one is unnecessary complexity until
    // real data justifies it.
    private static bool FitsWithinReserveCapital(
        PortfolioState portfolioState,
        decimal reserveAdjustedAvailableCapital,
        decimal runningTotalAllocatedThisScan,
        decimal candidateDollarAmount)
    {
        var projectedTotalDeployed = portfolioState.TotalDeployedCapital + runningTotalAllocatedThisScan + candidateDollarAmount;
        return projectedTotalDeployed <= reserveAdjustedAvailableCapital;
    }
}
