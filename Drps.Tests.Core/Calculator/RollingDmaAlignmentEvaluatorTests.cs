using Drps.Calculator.Dma.RollingDma;
using Drps.Shared.Models;

namespace Drps.Tests.Calculator;

public class RollingDmaAlignmentEvaluatorTests
{
    [Fact]
    public void EvaluateAlignment_FullyBullishStack_AllFourSlotsAligned()
    {
        var values = new RollingDmaAlignmentEvaluator.TermValues(
            Value5: 10m, Value15: 8m, Value30: 6m, Value60: 4m);

        var result = RollingDmaAlignmentEvaluator.EvaluateAlignment(values);

        Assert.True(result[DmaAlignmentTerm.Term5]); // 10 > 8
        Assert.True(result[DmaAlignmentTerm.Term15]); // 8 > 6
        Assert.True(result[DmaAlignmentTerm.Term30]); // 6 > 4
        // FullStackAligned mirrors Gate's own combined IsDmaAligned check exactly - the AND of
        // the other three (GateScanService.ResolveDmaAsync, Drps.Gate/Scoring/
        // GateScanService.cs:610-612) - not a fourth independent term.
        Assert.True(result[DmaAlignmentTerm.FullStackAligned]);
    }

    [Fact]
    public void EvaluateAlignment_Term5NotAligned_WhenShorterTermBelowLonger()
    {
        var values = new RollingDmaAlignmentEvaluator.TermValues(
            Value5: 5m, Value15: 8m, Value30: 6m, Value60: 4m);

        var result = RollingDmaAlignmentEvaluator.EvaluateAlignment(values);

        Assert.False(result[DmaAlignmentTerm.Term5]); // 5 is not > 8
        Assert.True(result[DmaAlignmentTerm.Term15]);
        Assert.True(result[DmaAlignmentTerm.Term30]);
        // FullStackAligned is the AND of all three - one false sub-term means it is false too,
        // even though the 30-vs-60 pairwise relation independently holds.
        Assert.False(result[DmaAlignmentTerm.FullStackAligned]);
    }

    [Fact]
    public void EvaluateAlignment_OnlyTerm30PairwiseFails_FullStackAlignedAlsoFalse()
    {
        var values = new RollingDmaAlignmentEvaluator.TermValues(
            Value5: 10m, Value15: 8m, Value30: 6m, Value60: 7m); // 30 (6) is not > 60 (7)

        var result = RollingDmaAlignmentEvaluator.EvaluateAlignment(values);

        Assert.True(result[DmaAlignmentTerm.Term5]);
        Assert.True(result[DmaAlignmentTerm.Term15]);
        Assert.False(result[DmaAlignmentTerm.Term30]);
        // AND propagates: FullStackAligned can never be true if Term30 is false.
        Assert.False(result[DmaAlignmentTerm.FullStackAligned]);
    }

    [Fact]
    public void EvaluateAlignment_AllThreePairwiseConditionsHold_FullStackAlignedTrueWithNoPriorValueNeeded()
    {
        // Confirms FullStackAligned requires no "previous" state at all (unlike the superseded
        // self-slope placeholder) - it's fully computable from this single snapshot alone, the
        // same way Gate's own IsDmaAligned is.
        var values = new RollingDmaAlignmentEvaluator.TermValues(
            Value5: 100m, Value15: 90m, Value30: 80m, Value60: 70m);

        var result = RollingDmaAlignmentEvaluator.EvaluateAlignment(values);

        Assert.True(result[DmaAlignmentTerm.FullStackAligned]);
    }

    [Fact]
    public void EvaluateAlignment_InsufficientHistoryOnValue60_Term30AndFullStackAlignedBothFalse()
    {
        // Value60 missing (insufficient history) - Term30's pairwise check against the 60-day
        // window can't be evaluated, and FullStackAligned (the AND of all three) is therefore
        // false regardless of Term5/Term15.
        var values = new RollingDmaAlignmentEvaluator.TermValues(
            Value5: 10m, Value15: 8m, Value30: 6m, Value60: null);

        var result = RollingDmaAlignmentEvaluator.EvaluateAlignment(values);

        Assert.True(result[DmaAlignmentTerm.Term5]);
        Assert.True(result[DmaAlignmentTerm.Term15]);
        Assert.False(result[DmaAlignmentTerm.Term30]); // can't compare 6 > null
        Assert.False(result[DmaAlignmentTerm.FullStackAligned]);
    }

    [Fact]
    public void EvaluateAlignment_InsufficientHistoryOnValue5_Term5AndFullStackAlignedBothFalse()
    {
        var values = new RollingDmaAlignmentEvaluator.TermValues(
            Value5: null, Value15: 8m, Value30: 6m, Value60: 4m);

        var result = RollingDmaAlignmentEvaluator.EvaluateAlignment(values);

        Assert.False(result[DmaAlignmentTerm.Term5]);
        Assert.True(result[DmaAlignmentTerm.Term15]);
        Assert.True(result[DmaAlignmentTerm.Term30]);
        // Term5's own condition is unresolvable, so the AND is false.
        Assert.False(result[DmaAlignmentTerm.FullStackAligned]);
    }

    [Fact]
    public void EvaluateAlignment_EqualAdjacentValues_NotStrictlyGreaterSoNotAligned()
    {
        // Strict ">" per Gate's own formula - equal values do not count as aligned.
        var values = new RollingDmaAlignmentEvaluator.TermValues(
            Value5: 10m, Value15: 10m, Value30: 10m, Value60: 10m);

        var result = RollingDmaAlignmentEvaluator.EvaluateAlignment(values);

        Assert.False(result[DmaAlignmentTerm.Term5]);
        Assert.False(result[DmaAlignmentTerm.Term15]);
        Assert.False(result[DmaAlignmentTerm.Term30]);
        Assert.False(result[DmaAlignmentTerm.FullStackAligned]);
    }
}
