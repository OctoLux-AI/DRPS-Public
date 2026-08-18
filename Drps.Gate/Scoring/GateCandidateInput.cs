namespace Drps.Gate.Scoring;

/// <summary>
/// One candidate's already-computed DMA/RSI/RVOL/ATR values plus their four
/// Is*VerifiedAsync results, as resolved by Drps.Calculator's DmaVerificationJoinService/
/// RsiVerificationJoinService/RvolVerificationJoinService/AtrVerificationJoinService before
/// GateQualityScorer ever runs. GateQualityScorer itself is a pure, synchronous function -
/// it does not call those join services or touch the database.
/// </summary>
public sealed record GateCandidateInput
{
    public required bool IsDmaAligned { get; init; }

    // Per-window verification (blast-radius scoping, CLAUDE.md 2026-08-04) - each is
    // independently resolved from DmaVerificationJoinService.IsDmaVerifiedAsync at that
    // window's own bar count, NOT a single ticker-wide "all four AND'd" boolean. A bad bar
    // 45 trading days old fails IsDma60Verified but does not touch IsDma5Verified/
    // IsDma15Verified/IsDma30Verified, since their own rolling lookbacks never include that
    // bar. GateQualityScorer's Tier 1 rejection currently gates on IsDma5Verified alone -
    // see its own doc comment for why.
    public required bool IsDma5Verified { get; init; }

    public required bool IsDma15Verified { get; init; }

    public required bool IsDma30Verified { get; init; }

    public required bool IsDma60Verified { get; init; }

    public required decimal RsiValue { get; init; }

    public required bool IsRsiVerified { get; init; }

    public required decimal RvolValue { get; init; }

    public required bool IsRvolVerified { get; init; }

    public required bool IsAtrVerified { get; init; }

    // Last clean pre-ex-div ATR reading, when applicable (Gate: Third Design Decision) - not
    // used by any gating check here, carried straight through onto GateQualityResult.
    public decimal? AtrCleanPreEventValue { get; init; }

    public required bool ScoredThroughExDividendEvent { get; init; }

    public required bool VerificationScopeLimited { get; init; }

    // True if any of the DMA/RSI/RVOL/ATR indicator rows that fed this candidate had
    // HasTiingoCorrectedClose == true - resolved by GateScanService from the provenance
    // fields on those rows, carried straight through GateQualityScorer onto GateScore.
    // HasUnverifiedPartialCreditData (GateScore's other data-quality field) is NOT part of
    // this input - GateQualityScorer derives it itself from IsRvolVerified/IsAtrVerified,
    // which this record already carries.
    public required bool HasTiingoCorrectedData { get; init; }

    // Resolved by GateScanService from EarningsLookupService.GetBlackoutStatusAsync - a hard
    // gate excluding the candidate from BUY (Earnings Blackout Gate Decision, CLAUDE.md
    // 2026-07-19), never folded into the composite score. Mutually exclusive: at most one of
    // these two is ever true.
    public required bool IsEarningsBlackoutActive { get; init; }

    public required bool IsEarningsDataUnverified { get; init; }

    // Sleeper Bucket (CLAUDE.md 2026-08-04) - resolved by GateScanService ONLY when
    // !IsDmaAligned (RsiSlope=ConfirmedPositive AND RsiConcavity=ConfirmedPositive AND
    // IsRsiSlopeVerifiedAsync); always false when DMA is aligned, since GateQualityScorer
    // never consults this field past the DmaNotAligned check. Carried straight through
    // GateQualityScorer onto GateQualityResult regardless of rejection reason, same
    // passthrough convention as every other field here - GateScanService, not
    // GateQualityScorer, decides what to do with it.
    public required bool IsSleeperEligible { get; init; }
}
