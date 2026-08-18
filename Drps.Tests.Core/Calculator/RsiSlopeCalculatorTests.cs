using Drps.Calculator.Rsi;

namespace Drps.Tests.Calculator;

public class RsiSlopeCalculatorTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // Hand-picked RSI sequence (not derived from RsiCalculator - RsiSlopeCalculator operates
    // directly on already-computed RSI values, so a synthetic sequence with a deliberate sign
    // change midway is more useful here than a real Wilder-smoothed series). Also reused by
    // RsiSlopeConfirmationEvaluatorTests and (via its own slope-derived sequence) by
    // RsiConcavityCalculatorTests/RsiConcavityConfirmationEvaluatorTests.
    private static readonly decimal[] RsiValues = { 50m, 55m, 58m, 60m, 63m, 61m, 59m, 65m, 70m, 68m };

    // Hand-calculated: RsiValues[i] - RsiValues[i-3] for i = 3..9.
    // 60-50=10, 63-55=8, 61-58=3, 59-60=-1, 65-63=2, 70-61=9, 68-59=9
    public static readonly decimal[] ExpectedSlopeLookback3 = { 10m, 8m, 3m, -1m, 2m, 9m, 9m };

    private static List<RsiSlopeCalculator.RsiSlopeInput> MakeSeries(IReadOnlyList<decimal> values) =>
        values
            .Select((v, i) => new RsiSlopeCalculator.RsiSlopeInput(FirstDay.AddDays(i), v))
            .ToList();

    [Fact]
    public void Compute_TenReadingsLookbackThree_MatchesHandCalculatedSlopeSequence()
    {
        var series = MakeSeries(RsiValues);

        var results = RsiSlopeCalculator.Compute(series, lookback: 3);

        Assert.Equal(ExpectedSlopeLookback3.Length, results.Count);
        for (var i = 0; i < ExpectedSlopeLookback3.Length; i++)
        {
            Assert.Equal(FirstDay.AddDays(i + 3), results[i].Date);
            Assert.Equal(3, results[i].Lookback);
            Assert.Equal(ExpectedSlopeLookback3[i], results[i].Value);
        }
    }

    [Fact]
    public void Compute_ExactlyLookbackReadings_ProducesNoResults()
    {
        // 3 readings with lookback 3 - index 3 (the first possible result) doesn't exist yet.
        var series = MakeSeries(RsiValues.Take(3).ToArray());

        var results = RsiSlopeCalculator.Compute(series, lookback: 3);

        Assert.Empty(results);
    }

    [Fact]
    public void Compute_OneMoreThanLookbackReadings_ProducesExactlyOneResult()
    {
        var series = MakeSeries(RsiValues.Take(4).ToArray()); // indices 0..3

        var results = RsiSlopeCalculator.Compute(series, lookback: 3);

        var result = Assert.Single(results);
        Assert.Equal(FirstDay.AddDays(3), result.Date);
        Assert.Equal(10m, result.Value); // RsiValues[3] - RsiValues[0] = 60 - 50
    }

    [Fact]
    public void Compute_NonPositiveLookback_Throws()
    {
        var series = MakeSeries(RsiValues);

        Assert.Throws<ArgumentOutOfRangeException>(() => RsiSlopeCalculator.Compute(series, lookback: 0));
    }
}
