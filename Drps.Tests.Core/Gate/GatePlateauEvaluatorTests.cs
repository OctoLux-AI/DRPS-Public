using Drps.Gate.Scoring;
using Drps.Shared.Models;

namespace Drps.Tests.Gate;

public class GatePlateauEvaluatorTests
{
    // [REDACTED FOR PUBLIC RELEASE] Placeholder fixture values, not DRPS's real shipped
    // tuning - see README.md's "What's intentionally not public" section. IsTriggered's own
    // tests are removed below since IsTriggered's real constants are redacted at the source
    // (GatePlateauEvaluator.cs); IsInadequate's tests survive since that method's logic reads
    // these caller-supplied parameters directly, not a redacted internal constant.
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

    private readonly GatePlateauEvaluator _evaluator = new(TestParameters);

    [Fact]
    public void IsInadequate_DmaNotAligned_ReturnsTrue()
    {
        // RSI/RVOL both comfortably clear their own bars - DMA misalignment alone must be
        // sufficient to fail the hard gate.
        var isInadequate = _evaluator.IsInadequate(
            isDmaAligned: false, isDmaFullyVerified: true, rsiValue: 55m, rvolValue: 2.25m);

        Assert.True(isInadequate);
    }

    [Fact]
    public void IsInadequate_DmaAlignedButNotFullyVerified_ReturnsTrue()
    {
        // DMA hard-gate is alignment AND verification together (Gate: Fifth Design Decision) -
        // aligned-but-unverified must fail it too, not just misalignment.
        var isInadequate = _evaluator.IsInadequate(
            isDmaAligned: true, isDmaFullyVerified: false, rsiValue: 55m, rvolValue: 2.25m);

        Assert.True(isInadequate);
    }

    [Fact]
    public void IsInadequate_RsiBelowLowerBound_ReturnsTrue()
    {
        // DMA and RVOL both pass - RSI below the fixture's lower bound alone must be
        // sufficient.
        var isInadequate = _evaluator.IsInadequate(
            isDmaAligned: true, isDmaFullyVerified: true, rsiValue: 30m, rvolValue: 2.25m);

        Assert.True(isInadequate);
    }

    [Fact]
    public void IsInadequate_RsiAboveUpperBound_ReturnsTrue()
    {
        // Same band check, opposite edge - RSI above the fixture's upper bound.
        var isInadequate = _evaluator.IsInadequate(
            isDmaAligned: true, isDmaFullyVerified: true, rsiValue: 90m, rvolValue: 2.25m);

        Assert.True(isInadequate);
    }

    [Fact]
    public void IsInadequate_RvolBelowFloor_ReturnsTrue()
    {
        // DMA and RSI both pass - RVOL below the fixture's floor alone must be sufficient.
        // Confirms this is a HARD reject here: under ordinary GateQualityScorer scoring, the
        // exact same RVOL value (below RvolFloorMultiple) only zeroes RvolQuality (contributes
        // 0 to the composite) - it never rejects the candidate outright. Plateau reassessment's
        // Inadequate check is a deliberate, explicit departure from that partial-credit
        // behavior (2026-07-18 decision block, point 3).
        var isInadequate = _evaluator.IsInadequate(
            isDmaAligned: true, isDmaFullyVerified: true, rsiValue: 55m, rvolValue: 0.8m);

        Assert.True(isInadequate);
    }

    [Fact]
    public void IsInadequate_AllThreeConditionsPass_ReturnsFalse()
    {
        // DMA aligned+verified, RSI in-band, RVOL at/above the floor - none of the three
        // Inadequate conditions apply, so the reassessment resolves to Reactivated.
        var isInadequate = _evaluator.IsInadequate(
            isDmaAligned: true, isDmaFullyVerified: true, rsiValue: 60m, rvolValue: 2.25m);

        Assert.False(isInadequate);
    }
}
