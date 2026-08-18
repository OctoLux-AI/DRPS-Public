using Drps.Shared.Models;

namespace Drps.Gate.Scoring;

/// <summary>
/// Pure, synchronous plateau-reassessment logic (2026-07-18 decision block, point 3) - no
/// database access, no async, same shape as GateQualityScorer/GateCompositeService. Separated
/// from GateScanService's own DB-resolution/write-path orchestration so every branch is
/// independently unit-testable without seeding a full indicator/Position fixture per case.
/// </summary>
public class GatePlateauEvaluator
{
    private readonly GateParameters _parameters;

    public GatePlateauEvaluator(GateParameters parameters)
    {
        _parameters = parameters;
    }

    // [REDACTED FOR PUBLIC RELEASE]
    // Trigger rule shape: ATR-cross OR N-trading-days-elapsed, whichever comes first (a plain
    // OR, not a priority order). The two calibrated constants (the ATR multiple and the
    // trading-day threshold) are proprietary and not included in this public repository - see
    // README.md's "What's intentionally not public" section.
    public bool IsTriggered(decimal currentPrice, decimal baselinePrice, decimal currentAtr, int tradingDaysElapsedSinceBaseline)
    {
        throw new NotImplementedException("Redacted for public release - see README.md");
    }

    /// <summary>
    /// Inadequate = DMA hard-gate fails OR RSI falls outside [RsiLowerBound, RsiUpperBound] OR
    /// RVOL falls below RvolFloorMultiple - exactly these three conditions, OR'd together.
    ///
    /// isDmaAligned/isDmaFullyVerified are the caller's own already-resolved Tier 1 DMA check
    /// (GateScanService.ResolveDmaAsync/IsDmaFullyVerifiedAsync) - reused as-is, not
    /// reimplemented here.
    ///
    /// RSI's band check is a plain boundary comparison against the raw value - deliberately NOT
    /// GateQualityScorer's peaked 0.0-1.0 quality curve (that curve answers "how good"; this
    /// answers "in band at all").
    ///
    /// RVOL below the floor is a HARD cutoff here - deliberately NOT GateQualityScorer's
    /// partial-credit/half-weight treatment (Gate: First Design Decision's Tier 2 rule, which
    /// never rejects a candidate outright for low RVOL). The 2026-07-18 decision block calls
    /// this out as an explicit, intentional departure specific to plateau reassessment - RVOL
    /// below the floor here means Inadequate, full stop, unlike ordinary composite scoring.
    /// </summary>
    public bool IsInadequate(bool isDmaAligned, bool isDmaFullyVerified, decimal rsiValue, decimal rvolValue)
    {
        var dmaHardGateFails = !isDmaAligned || !isDmaFullyVerified;
        var rsiOutOfBand = rsiValue < _parameters.RsiLowerBound || rsiValue > _parameters.RsiUpperBound;
        var rvolBelowFloor = rvolValue < _parameters.RvolFloorMultiple;

        return dmaHardGateFails || rsiOutOfBand || rvolBelowFloor;
    }
}
