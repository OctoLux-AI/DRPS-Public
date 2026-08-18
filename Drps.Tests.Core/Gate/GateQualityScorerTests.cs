using Drps.Gate.Scoring;
using Drps.Shared.Models;

namespace Drps.Tests.Gate;

// Most of this class's original coverage exercised GateQualityScorer's RSI/RVOL quality-curve
// formulas directly, or scenarios that reach them - both are redacted for public release (see
// GateQualityScorer.cs). What survives is the Tier 1 gating coverage that rejects a candidate
// before either curve is ever evaluated - DMA alignment/verification and RSI verification
// checks, which run ahead of the redacted curve math in Score's own execution order.
public class GateQualityScorerTests
{
    private static readonly GateCandidateInput Baseline = new()
    {
        IsDmaAligned = true,
        IsDma5Verified = true,
        IsDma15Verified = true,
        IsDma30Verified = true,
        IsDma60Verified = true,
        RsiValue = 55m,
        IsRsiVerified = true,
        RvolValue = 2.0m,
        IsRvolVerified = true,
        IsAtrVerified = true,
        AtrCleanPreEventValue = null,
        ScoredThroughExDividendEvent = false,
        VerificationScopeLimited = false,
        HasTiingoCorrectedData = false,
        IsEarningsBlackoutActive = false,
        IsEarningsDataUnverified = false,
        IsSleeperEligible = false
    };

    // [REDACTED FOR PUBLIC RELEASE] Placeholder fixture values, not DRPS's real shipped
    // tuning - see README.md's "What's intentionally not public" section.
    private static readonly GateParameters TestParameters = new()
    {
        RsiLowerBound = 45m,
        RsiPeak = 55m,
        RsiUpperBound = 65m,
        RsiFloorQuality = 0.75m,
        RvolFloorMultiple = 1.2m,
        RvolCeilingMultiple = 2.8m,
        RvolFullWeight = 0.30m,
        RvolHalfWeight = 0.15m,
        RsiCompositeWeight = 0.70m,
        BuyThreshold = 0.85m,
        WatchThreshold = 0.75m,
        ExitThreshold = 0.70m,
        NoBuySessionCount = 2
    };

    private readonly GateQualityScorer _scorer = new(TestParameters);

    [Fact]
    public void Score_DmaNotAligned_RejectsBeforeEvaluatingAnyOtherIndicator()
    {
        var input = Baseline with { IsDmaAligned = false };

        var result = _scorer.Score(input, hasBackstopStopControl: true);

        Assert.True(result.IsRejected);
        Assert.Equal(GateRejectionReason.DmaNotAligned, result.RejectionReason);
        Assert.Null(result.RsiQuality);
        Assert.Null(result.RvolQuality);
        Assert.Null(result.RvolEffectiveWeight);
        Assert.False(result.BucketCappedAtWatch);
    }

    [Fact]
    public void Score_DmaNotAlignedWithSleeperEligibleTrue_CarriesEligibilityThroughOnRejection()
    {
        // Sleeper Bucket (CLAUDE.md 2026-08-04) - GateQualityScorer makes no decision based on
        // IsSleeperEligible itself (GateScanService does); this proves the passthrough alone,
        // same "carry why, not null out silently" convention as every other passthrough fact.
        var input = Baseline with { IsDmaAligned = false, IsSleeperEligible = true };

        var result = _scorer.Score(input, hasBackstopStopControl: true);

        Assert.True(result.IsRejected);
        Assert.Equal(GateRejectionReason.DmaNotAligned, result.RejectionReason);
        Assert.True(result.IsSleeperEligible);
    }

    [Fact]
    public void Score_DmaNotAlignedWithSleeperEligibleFalse_CarriesEligibilityThroughOnRejection()
    {
        var input = Baseline with { IsDmaAligned = false, IsSleeperEligible = false };

        var result = _scorer.Score(input, hasBackstopStopControl: true);

        Assert.True(result.IsRejected);
        Assert.Equal(GateRejectionReason.DmaNotAligned, result.RejectionReason);
        Assert.False(result.IsSleeperEligible);
    }

    [Fact]
    public void Score_Dma5Unverified_Rejects()
    {
        // Blast-radius scoping (CLAUDE.md 2026-08-04) - DMA-5 is the sole window Tier 1
        // gates on. DMA-15/30/60 stay verified here (Baseline default) to prove the
        // rejection is driven specifically by DMA-5, not incidentally by the others.
        var input = Baseline with { IsDma5Verified = false };

        var result = _scorer.Score(input, hasBackstopStopControl: true);

        Assert.True(result.IsRejected);
        Assert.Equal(GateRejectionReason.DmaNotVerified, result.RejectionReason);
        Assert.Null(result.RsiQuality);
    }

    [Fact]
    public void Score_RsiUnverified_Rejects()
    {
        var input = Baseline with { IsRsiVerified = false };

        var result = _scorer.Score(input, hasBackstopStopControl: true);

        Assert.True(result.IsRejected);
        Assert.Equal(GateRejectionReason.RsiNotVerified, result.RejectionReason);
        Assert.Null(result.RsiQuality);
        // DMA already passed by this point - its passthrough facts are still carried, all
        // four windows independently.
        Assert.True(result.IsDmaAligned);
        Assert.True(result.IsDma5VerifiedAsyncResult);
        Assert.True(result.IsDma15VerifiedAsyncResult);
        Assert.True(result.IsDma30VerifiedAsyncResult);
        Assert.True(result.IsDma60VerifiedAsyncResult);
    }
}
