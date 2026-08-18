using Drps.Calculator.Dma.RollingDma;

namespace Drps.Tests.Calculator;

public class RollingDmaCalculatorTests
{
    [Fact]
    public void ApplyIncrementalUpdate_WindowNotYetFull_AccumulatesWithoutDropping()
    {
        // 3 bars folded in so far (sum=6, avg 2 each), term=5 - window still filling.
        var result = RollingDmaCalculator.ApplyIncrementalUpdate(
            priorRollingSum: 6m, priorBarCount: 3, term: 5, newClose: 4m, droppedClose: null);

        Assert.Equal(10m, result.RollingSum); // 6 + 4
        Assert.Equal(4, result.BarCount);
        Assert.Null(result.Value); // still short of the 5-bar window
    }

    [Fact]
    public void ApplyIncrementalUpdate_WindowJustBecomesFull_EmitsValue()
    {
        var result = RollingDmaCalculator.ApplyIncrementalUpdate(
            priorRollingSum: 10m, priorBarCount: 4, term: 5, newClose: 5m, droppedClose: null);

        Assert.Equal(15m, result.RollingSum); // 1+2+3+4+5
        Assert.Equal(5, result.BarCount);
        Assert.Equal(3m, result.Value); // avg(1..5)
    }

    [Fact]
    public void ApplyIncrementalUpdate_WindowAlreadyFull_DropsOldestAddsNewest()
    {
        // Window (2,3,4,5,6), sum=20, term=5. New bar 7 arrives, oldest (2) drops out.
        var result = RollingDmaCalculator.ApplyIncrementalUpdate(
            priorRollingSum: 20m, priorBarCount: 5, term: 5, newClose: 7m, droppedClose: 2m);

        Assert.Equal(25m, result.RollingSum); // 20 - 2 + 7 = 3+4+5+6+7
        Assert.Equal(5, result.BarCount); // pinned at Term
        Assert.Equal(5m, result.Value); // avg(3..7)
    }

    [Fact]
    public void ApplyIncrementalUpdate_WindowFullButNoDroppedCloseProvided_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            RollingDmaCalculator.ApplyIncrementalUpdate(
                priorRollingSum: 20m, priorBarCount: 5, term: 5, newClose: 7m, droppedClose: null));
    }

    [Fact]
    public void ComputeSumFromScratch_FewerClosesThanTerm_ReturnsPartialSumAndNullValue()
    {
        var closes = new List<decimal> { 1m, 2m, 3m };
        var result = RollingDmaCalculator.ComputeSumFromScratch(closes, term: 5);

        Assert.Equal(6m, result.RollingSum);
        Assert.Equal(3, result.BarCount);
        Assert.Null(result.Value);
    }

    [Fact]
    public void ComputeSumFromScratch_ExactlyEnoughCloses_ComputesFullWindow()
    {
        var closes = new List<decimal> { 1m, 2m, 3m, 4m, 5m };
        var result = RollingDmaCalculator.ComputeSumFromScratch(closes, term: 5);

        Assert.Equal(15m, result.RollingSum);
        Assert.Equal(5, result.BarCount);
        Assert.Equal(3m, result.Value);
    }

    [Fact]
    public void ComputeSumFromScratch_MoreClosesThanTerm_OnlySumsTrailingWindow()
    {
        // 10 closes (1..10); term=5 should only sum the trailing 5 (6..10), not the full history.
        var closes = Enumerable.Range(1, 10).Select(i => (decimal)i).ToList();
        var result = RollingDmaCalculator.ComputeSumFromScratch(closes, term: 5);

        Assert.Equal(40m, result.RollingSum); // 6+7+8+9+10
        Assert.Equal(5, result.BarCount);
        Assert.Equal(8m, result.Value);
    }
}
