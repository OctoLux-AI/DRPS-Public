namespace Drps.Gate.Scoring;

public enum GateRejectionReason
{
    // Tier 1, binary - perfect 5>15>30>60 alignment or rejected, no partial credit
    // (Gate: Fifth Design Decision).
    DmaNotAligned,

    // Blast-radius scoping (CLAUDE.md 2026-08-04): gates on DMA-5's own verification only,
    // not the old "all four windows AND'd" check. DMA-5 is the shortest/most-recent window,
    // so a bad bar landing inside it carries the highest per-bar weight (1/5) and the
    // tightest blast radius - the one case still worth a hard, no-partial-credit reject.
    // DMA-15/30/60 verification is still resolved and recorded on GateScore for every
    // candidate that reaches scoring, but no longer independently blocks it.
    DmaNotVerified,

    // Tier 1, non-negotiable - unverified RSI disqualifies a candidate entirely, no partial
    // credit (Gate: First Design Decision).
    RsiNotVerified,

    // Covers both RSI outside the [50,70] passable band and the below-floor safety-net
    // check on the computed quality value itself - see GateQualityScorer's own comment on
    // why these collapse to one reason.
    RsiOutsideQualityBand,

    // Tier 2 trade-safety precondition, not a quality score - an unverified ATR value with
    // no non-ATR-dependent backstop stop control means no safe stop mechanism exists
    // (Gate: First Design Decision).
    AtrUnverifiedNoBackstop
}

/// <summary>
/// GateQualityScorer's output for one candidate: quality scores and gating outcome, nothing
/// else. Deliberately does NOT include CompositeScore or Bucket - those consume this result
/// in a later stage, per this task's own scope boundary. A rejected candidate still carries
/// every passthrough fact (IsDmaAligned, IsDmaVerifiedAsyncResult, AtrCleanPreEventValue,
/// ScoredThroughExDividendEvent, VerificationScopeLimited) plus whichever quality values were
/// actually computed before the rejection - RejectionReason records why, nothing is silently
/// nulled out without explanation.
/// </summary>
public sealed record GateQualityResult
{
    public GateRejectionReason? RejectionReason { get; init; }

    public bool IsRejected => RejectionReason is not null;

    public bool IsDmaAligned { get; init; }

    // Per-window pass-through (blast-radius scoping, CLAUDE.md 2026-08-04) - replaces the
    // old single IsDmaVerifiedAsyncResult bool. All four are always carried through, even on
    // a rejected result (same "carry why, not just null out silently" convention this record
    // already applies to every other passthrough fact) - a rejection for a different reason
    // (e.g. RsiNotVerified) must still let a reader see DMA's own per-window state.
    public bool IsDma5VerifiedAsyncResult { get; init; }

    public bool IsDma15VerifiedAsyncResult { get; init; }

    public bool IsDma30VerifiedAsyncResult { get; init; }

    public bool IsDma60VerifiedAsyncResult { get; init; }

    public decimal? RsiQuality { get; init; }

    public decimal? RvolQuality { get; init; }

    // 0.30 (full) or 0.15 (half, when RVOL is unverified) - the actual weight to apply in
    // the composite formula built by the next task, not computed here.
    public decimal? RvolEffectiveWeight { get; init; }

    public decimal? AtrCleanPreEventValue { get; init; }

    public bool ScoredThroughExDividendEvent { get; init; }

    public bool VerificationScopeLimited { get; init; }

    // Set when ATR is unverified but a non-ATR-dependent backstop stop control is
    // available - candidate proceeds but bucket assignment (next task) must cap it at
    // WATCH/NEUTRAL, never BUY (Gate: First Design Decision).
    public bool BucketCappedAtWatch { get; init; }

    // Data-quality provenance carried straight onto GateScore - see GateScore's own doc
    // comment for why these are two separate bools, not one combined flag. Pass-through from
    // GateCandidateInput.HasTiingoCorrectedData.
    public bool HasTiingoCorrectedData { get; init; }

    // Computed here, not passed in - true if RVOL is unverified (half weight) or ATR is
    // unverified (implies a backstop existed, since no-backstop already rejected above).
    // DMA/RSI can never drive this true: both are Tier 1, hard-reject-on-unverified, so any
    // result reaching this point (rejected or not) already had them independently checked.
    public bool HasUnverifiedPartialCreditData { get; init; }

    // Pass-through from GateCandidateInput, carried straight onto GateScore - see
    // GateCandidateInput's own doc comment. Folded into BucketCappedAtWatch above (the
    // earnings gate is binary pass/fail, never part of the RSI/RVOL composite), but kept as
    // its own field here too so GateScore's EarningsDataUnverified/IsEarningsBlackoutActive
    // columns stay distinguishable from any other reason a candidate might be capped.
    public bool IsEarningsBlackoutActive { get; init; }

    public bool IsEarningsDataUnverified { get; init; }

    // Pass-through from GateCandidateInput.IsSleeperEligible (Sleeper Bucket, CLAUDE.md
    // 2026-08-04) - carried through unconditionally, same convention as every other
    // passthrough fact here. Only meaningful when RejectionReason == DmaNotAligned;
    // GateScanService is the one that acts on it (writing a Bucket.Sleeper row instead of
    // discarding), GateQualityScorer itself makes no decision based on it.
    public bool IsSleeperEligible { get; init; }
}
