using Drps.Shared.Models;
using Drps.Shared.Scoring;

namespace Drps.Gate.Scoring;

/// <summary>
/// Combines a non-rejected GateQualityResult into a single CompositeScore and bucket
/// assignment, including a hysteresis exit floor and NoBuy list. Pure and synchronous - no
/// database access, no async, no internal DateTime.Now call (matches Gate's clock-injection
/// discipline; the caller supplies asOf). The composite-weighting formula and bucket
/// thresholds are redacted below for public release - see README.md.
///
/// Every tunable threshold below is read from the caller-supplied GateParameters row rather
/// than a hardcoded literal - see GateQualityScorer's own doc comment for why.
/// </summary>
public class GateCompositeService
{
    private readonly GateParameters _parameters;

    public GateCompositeService(GateParameters parameters)
    {
        _parameters = parameters;
    }

    public GateCompositeResult Score(
        GateQualityResult qualityResult,
        bool isCurrentlyHeld,
        DateTime asOf,
        DateTime? noBuyExpiration = null)
    {
        // Rejected candidates never reach composite scoring (Gate: Sixth Design Decision's
        // scope boundary) - a caller passing one is a bug upstream, not a case to score
        // silently into some default bucket.
        if (qualityResult.IsRejected)
        {
            throw new ArgumentException(
                $"GateCompositeService cannot bucket a rejected candidate (reason: {qualityResult.RejectionReason}). " +
                "Rejected candidates never reach composite scoring.",
                nameof(qualityResult));
        }

        var compositeScore = ComputeCompositeScore(qualityResult);
        var isOnNoBuyList = noBuyExpiration.HasValue && asOf < noBuyExpiration.Value;

        var bucket = DetermineBucket(
            compositeScore, qualityResult.BucketCappedAtWatch, isOnNoBuyList, isCurrentlyHeld);

        return new GateCompositeResult
        {
            CompositeScore = compositeScore,
            Bucket = bucket,
            NoBuyListExpiration = bucket == GateBucket.Exit ? ComputeNoBuyExpiration(asOf) : null
        };
    }

    // [REDACTED FOR PUBLIC RELEASE]
    // DRPS's composite-weighting formula (how RSI and RVOL quality scores combine into one
    // number) is proprietary and not included in this public repository - see README.md's
    // "What's intentionally not public" section.
    private decimal ComputeCompositeScore(GateQualityResult result)
    {
        throw new NotImplementedException("Redacted for public release - see README.md");
    }

    // [REDACTED FOR PUBLIC RELEASE]
    // DRPS's bucket-threshold values (Buy/Watch/Exit cutoffs) are proprietary and not included
    // in this public repository - see README.md's "What's intentionally not public" section.
    // The bucket shape itself (Buy requires clearing the top threshold, uncapped, and clear of
    // an active NoBuy hold; Exit is a held-position-only hysteresis floor below the entry
    // thresholds; everything else falls to Watch or Neutral) is architecture, not a number, and
    // is described in README.md instead.
    private GateBucket DetermineBucket(
        decimal compositeScore, bool bucketCappedAtWatch, bool isOnNoBuyList, bool isCurrentlyHeld)
    {
        throw new NotImplementedException("Redacted for public release - see README.md");
    }

    // NoBuySessionCount full trading sessions from asOf - see NoBuyExpirationCalculator's own
    // doc comment for the full rule. Extracted to Drps.Shared so LedgerPositionStateProvider
    // can compute the identical expiration from a stored CoolDownStartDate.
    private DateTime ComputeNoBuyExpiration(DateTime asOf) =>
        NoBuyExpirationCalculator.ComputeExpiration(asOf, _parameters.NoBuySessionCount);
}
