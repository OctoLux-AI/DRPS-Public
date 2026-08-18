using Drps.Calculator.Dma;

namespace Drps.Tests.Calculator;

public class DmaCalculatorTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // closes = 1, 2, 3, ..., count - deterministic so every expected rolling average can be
    // computed by hand (sum of an arithmetic sequence) rather than duplicating the
    // implementation under test.
    private static List<DmaCalculator.DmaBarInput> MakeBars(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new DmaCalculator.DmaBarInput(FirstDay.AddDays(i), i + 1m))
            .ToList();

    [Fact]
    public void Compute_EmptyInput_ReturnsNoResults()
    {
        var results = DmaCalculator.Compute(Array.Empty<DmaCalculator.DmaBarInput>());

        Assert.Empty(results);
    }

    [Fact]
    public void Compute_FewerBarsThanSmallestWindow_ReturnsNoResults()
    {
        var bars = MakeBars(4); // smallest window is 5

        var results = DmaCalculator.Compute(bars);

        Assert.Empty(results);
    }

    [Fact]
    public void Compute_ExactlySmallestWindow_EmitsOneDma5ValueForLastBarOnly()
    {
        var bars = MakeBars(5); // closes 1..5

        var results = DmaCalculator.Compute(bars);

        var result = Assert.Single(results);
        Assert.Equal(5, result.Window);
        Assert.Equal(bars[4].Date, result.Date);
        Assert.Equal(3m, result.Value); // (1+2+3+4+5)/5
    }

    [Fact]
    public void Compute_KnownSequence_Dma5MatchesHandComputedRollingAverageForEveryEligibleBar()
    {
        var bars = MakeBars(8); // closes 1..8

        var results = DmaCalculator.Compute(bars);
        var dma5 = results.Where(r => r.Window == 5).OrderBy(r => r.Date).ToList();

        var expected = new[] { 3m, 4m, 5m, 6m }; // avg(1..5), avg(2..6), avg(3..7), avg(4..8)
        Assert.Equal(expected.Length, dma5.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(bars[i + 4].Date, dma5[i].Date);
            Assert.Equal(expected[i], dma5[i].Value);
        }
    }

    [Fact]
    public void Compute_FewerThan60Bars_NeverEmitsDma60()
    {
        var bars = MakeBars(59);

        var results = DmaCalculator.Compute(bars);

        Assert.DoesNotContain(results, r => r.Window == 60);
    }

    [Fact]
    public void Compute_Exactly60Bars_EmitsExactlyOneDma60ValueOnTheSixtiethBar()
    {
        var bars = MakeBars(60); // closes 1..60

        var results = DmaCalculator.Compute(bars);
        var dma60 = Assert.Single(results, r => r.Window == 60);

        Assert.Equal(bars[59].Date, dma60.Date);
        Assert.Equal(30.5m, dma60.Value); // (1+60)*60/2/60
    }

    [Fact]
    public void Compute_65Bars_Dma60RollsForwardCorrectlyAndStaysAbsentForEarlierBars()
    {
        var bars = MakeBars(65); // closes 1..65

        var results = DmaCalculator.Compute(bars);
        var dma60 = results.Where(r => r.Window == 60).OrderBy(r => r.Date).ToList();

        // Bars are 0-indexed; DMA-60 becomes eligible starting at bar index 59 (the 60th bar)
        // and there are 65 - 60 + 1 = 6 eligible bars total.
        Assert.Equal(6, dma60.Count);
        Assert.Equal(bars[59].Date, dma60[0].Date);
        Assert.Equal(30.5m, dma60[0].Value); // avg(1..60)
        Assert.Equal(bars[64].Date, dma60[^1].Date);
        Assert.Equal(35.5m, dma60[^1].Value); // avg(6..65)

        // No DMA-60 value exists for any bar before the 60th.
        Assert.All(dma60, r => Assert.True(r.Date >= bars[59].Date));
    }

    [Fact]
    public void Compute_70Bars_LastBarEmitsAllFourWindowsWithCorrectValues()
    {
        var bars = MakeBars(70); // closes 1..70

        var results = DmaCalculator.Compute(bars);
        var lastDate = bars[^1].Date;
        var lastBarResults = results.Where(r => r.Date == lastDate).ToDictionary(r => r.Window, r => r.Value);

        Assert.Equal(4, lastBarResults.Count);
        Assert.Equal(68m, lastBarResults[5]);   // avg(66..70)
        Assert.Equal(63m, lastBarResults[15]);  // avg(56..70)
        Assert.Equal(55.5m, lastBarResults[30]); // avg(41..70)
        Assert.Equal(40.5m, lastBarResults[60]); // avg(11..70)
    }

    [Fact]
    public void Compute_70Bars_TotalResultCountMatchesSumOfEligibleBarsPerWindow()
    {
        var bars = MakeBars(70);

        var results = DmaCalculator.Compute(bars);

        // n - window + 1 eligible bars per window, summed across all four windows.
        var expectedCount = (70 - 5 + 1) + (70 - 15 + 1) + (70 - 30 + 1) + (70 - 60 + 1);
        Assert.Equal(expectedCount, results.Count);
    }
}
