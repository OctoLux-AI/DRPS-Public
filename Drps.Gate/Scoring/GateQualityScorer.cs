using Drps.Shared.Models;

namespace Drps.Gate.Scoring;

/// <summary>
/// Per-indicator quality-scoring and tier-gating for one Gate candidate, implementing the
/// Tier 1/Tier 2 policy (Gate: First Design Decision) and the locked RSI/RVOL quality curves
/// (Gate: Fifth Design Decision, and the RVOL curve locked 2026-07-15). Pure and synchronous
/// - no database access, no async. Deliberately does not compute CompositeScore or assign a
/// Bucket; both consume this result in a later stage (GateCompositeService's own weighting
/// logic is not implemented here). The RSI/RVOL quality-curve formulas below are redacted
/// for public release - see README.md.
///
/// Every tunable threshold below is read from the caller-supplied GateParameters row rather
/// than a hardcoded literal - matches Gate's clock-injection discipline (a scan-time value
/// supplied by the caller, not baked into this class) and lets GateScanService fail closed
/// when no active GateParameters row exists, instead of this class silently falling back to
/// a bootstrap default.
/// </summary>
public class GateQualityScorer
{
    // Curve invariants, not tunable parameters - GateParameters has no column for these
    // because they are definitional, not calibratable: the peak of a 0.0-1.0 quality curve
    // is always 1.0, and RsiBoundQuality is the same value as GateParameters.RsiFloorQuality
    // by design (Gate: Fifth Design Decision's anchors make the curve's boundary quality and
    // the Tier 1 hard-reject floor the same number - that identity is what makes the passable
    // range work out to exactly [RsiLowerBound, RsiUpperBound]).
    private const decimal RsiPeakQuality = 1.0m;

    private readonly GateParameters _parameters;

    public GateQualityScorer(GateParameters parameters)
    {
        _parameters = parameters;
    }

    public GateQualityResult Score(GateCandidateInput input, bool hasBackstopStopControl)
    {
        // Tier 1 - DMA: binary gate, no quality curve, no partial credit. Either failure
        // rejects the candidate before any other indicator is even looked at.
        //
        // LIVE-DATA NOTE (GateScores, 2026-08-07 through 2026-08-11 - no dedicated CLAUDE.md
        // audit block exists for this, cited by date range instead): this gate selects
        // specifically for a "quiet, orderly" cascading trend (5>15>30>60 all aligned) - the
        // opposite shape from a volume-shock/breakout event. A separate live audit over the
        // same window found each ticker's own single best-RSI day and single best-RVOL day
        // have never once landed on the same date, suggesting DMA's alignment requirement and
        // a genuinely high RVOL reading may be in structural tension (a trend orderly enough
        // to pass this gate tends not to be the same day volume is meaningfully elevated).
        // OPEN / UNSTUDIED: whether this tension is a real, general property of the strategy's
        // universe or an artifact of the small watchlist/short observation window studied so
        // far - not established either way. Observational only; no gate logic changed.
        if (!input.IsDmaAligned)
        {
            return Reject(input, GateRejectionReason.DmaNotAligned);
        }

        // Tier 1 - DMA verification: blast-radius scoped (CLAUDE.md 2026-08-04) - gates on
        // DMA-5's own window only, not the old "all four AND'd" check. See
        // GateRejectionReason.DmaNotVerified's own doc comment for the full reasoning.
        // DMA-15/30/60 are NOT checked here - they no longer block Tier 1 by themselves,
        // deliberately, and are carried through as data on every result below regardless of
        // outcome.
        if (!input.IsDma5Verified)
        {
            return Reject(input, GateRejectionReason.DmaNotVerified);
        }

        // Tier 1 - RSI: non-negotiable, full verification required, no partial credit.
        if (!input.IsRsiVerified)
        {
            return Reject(input, GateRejectionReason.RsiNotVerified);
        }

        var rsiQuality = ComputeRsiQuality(input.RsiValue);
        if (rsiQuality is null || rsiQuality.Value < _parameters.RsiFloorQuality)
        {
            // rsiQuality is null when RsiValue falls outside [RsiLowerBound,RsiUpperBound]
            // entirely (undefined for this curve). The < RsiFloorQuality branch guards the
            // interpolation math itself - unreachable given the exact anchor values above,
            // but explicit per this task's own requirement rather than assumed safe.
            return Reject(input, GateRejectionReason.RsiOutsideQualityBand, rsiQuality);
        }

        // Tier 2 - RVOL: partial credit, never rejects a candidate outright.
        var rvolQuality = ComputeRvolQuality(input.RvolValue);
        var rvolEffectiveWeight = input.IsRvolVerified ? _parameters.RvolFullWeight : _parameters.RvolHalfWeight;

        // Tier 2 - ATR: a trade-safety precondition, not a quality score. Unverified ATR
        // without a non-ATR-dependent backstop means no safe stop mechanism exists at all.
        var bucketCappedAtWatch = false;
        if (!input.IsAtrVerified)
        {
            if (!hasBackstopStopControl)
            {
                return Reject(
                    input, GateRejectionReason.AtrUnverifiedNoBackstop, rsiQuality, rvolQuality, rvolEffectiveWeight);
            }

            bucketCappedAtWatch = true;
        }

        // Earnings Blackout Gate Decision (CLAUDE.md 2026-07-19) - a hard, binary gate, never
        // folded into RsiQuality/RvolQuality/CompositeScore above. Both an active blackout and
        // an unverified/stale/missing check exclude the candidate from BUY the same way
        // ATR's unverified-with-backstop case does (capped at WATCH/NEUTRAL, never rejected
        // outright) - same fail-closed handling for "genuinely blocked" and "we don't know",
        // per this decision's own explicit instruction. The candidate is still scored and
        // persisted (unlike DMA/RSI's Tier 1 reject-outright rule) so a human can see it.
        if (input.IsEarningsBlackoutActive || input.IsEarningsDataUnverified)
        {
            bucketCappedAtWatch = true;
        }

        return new GateQualityResult
        {
            RejectionReason = null,
            IsDmaAligned = input.IsDmaAligned,
            IsDma5VerifiedAsyncResult = input.IsDma5Verified,
            IsDma15VerifiedAsyncResult = input.IsDma15Verified,
            IsDma30VerifiedAsyncResult = input.IsDma30Verified,
            IsDma60VerifiedAsyncResult = input.IsDma60Verified,
            RsiQuality = rsiQuality,
            RvolQuality = rvolQuality,
            RvolEffectiveWeight = rvolEffectiveWeight,
            AtrCleanPreEventValue = input.AtrCleanPreEventValue,
            ScoredThroughExDividendEvent = input.ScoredThroughExDividendEvent,
            VerificationScopeLimited = input.VerificationScopeLimited,
            BucketCappedAtWatch = bucketCappedAtWatch,
            HasTiingoCorrectedData = input.HasTiingoCorrectedData,
            HasUnverifiedPartialCreditData = !input.IsRvolVerified || !input.IsAtrVerified,
            IsEarningsBlackoutActive = input.IsEarningsBlackoutActive,
            IsEarningsDataUnverified = input.IsEarningsDataUnverified,
            IsSleeperEligible = input.IsSleeperEligible
        };
    }

    private static GateQualityResult Reject(
        GateCandidateInput input,
        GateRejectionReason reason,
        decimal? rsiQuality = null,
        decimal? rvolQuality = null,
        decimal? rvolEffectiveWeight = null)
    {
        return new GateQualityResult
        {
            RejectionReason = reason,
            IsDmaAligned = input.IsDmaAligned,
            IsDma5VerifiedAsyncResult = input.IsDma5Verified,
            IsDma15VerifiedAsyncResult = input.IsDma15Verified,
            IsDma30VerifiedAsyncResult = input.IsDma30Verified,
            IsDma60VerifiedAsyncResult = input.IsDma60Verified,
            RsiQuality = rsiQuality,
            RvolQuality = rvolQuality,
            RvolEffectiveWeight = rvolEffectiveWeight,
            AtrCleanPreEventValue = input.AtrCleanPreEventValue,
            ScoredThroughExDividendEvent = input.ScoredThroughExDividendEvent,
            VerificationScopeLimited = input.VerificationScopeLimited,
            BucketCappedAtWatch = false,
            HasTiingoCorrectedData = input.HasTiingoCorrectedData,
            HasUnverifiedPartialCreditData = !input.IsRvolVerified || !input.IsAtrVerified,
            IsEarningsBlackoutActive = input.IsEarningsBlackoutActive,
            IsEarningsDataUnverified = input.IsEarningsDataUnverified,
            IsSleeperEligible = input.IsSleeperEligible
        };
    }

    // [REDACTED FOR PUBLIC RELEASE]
    // DRPS's RSI quality-curve formula (curve shape, anchor points, interpolation method) is
    // proprietary and not included in this public repository - see README.md's "What's
    // intentionally not public" section. The method signature and its caller (Score, above)
    // are left intact so the surrounding Tier 1/Tier 2 gating architecture stays visible and
    // buildable; only the tuned calculation itself is redacted.
    private decimal? ComputeRsiQuality(decimal rsi)
    {
        throw new NotImplementedException("Redacted for public release - see README.md");
    }

    // [REDACTED FOR PUBLIC RELEASE]
    // DRPS's RVOL quality-curve formula (floor/ceiling calibration, ramp shape) is proprietary
    // and not included in this public repository - see README.md's "What's intentionally not
    // public" section.
    private decimal ComputeRvolQuality(decimal rvol)
    {
        throw new NotImplementedException("Redacted for public release - see README.md");
    }
}
