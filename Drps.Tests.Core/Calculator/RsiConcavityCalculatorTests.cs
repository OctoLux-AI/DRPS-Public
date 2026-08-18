using Drps.Calculator.Rsi;

namespace Drps.Tests.Calculator;

public class RsiConcavityCalculatorTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // The slope sequence hand-calculated in RsiSlopeCalculatorTests
    // (ExpectedSlopeLookback3 = 10, 8, 3, -1, 2, 9, 9 at day offsets 3..9).
    private static readonly decimal[] SlopeValues = { 10m, 8m, 3m, -1m, 2m, 9m, 9m };
    private const int SlopeStartOffset = 3;

    // Hand-calculated: SlopeValues[i] - SlopeValues[i-1].
    // 8-10=-2, 3-8=-5, -1-3=-4, 2-(-1)=3, 9-2=7, 9-9=0
    public static readonly decimal[] ExpectedConcavity = { -2m, -5m, -4m, 3m, 7m, 0m };

    private static List<RsiConcavityCalculator.RsiConcavityInput> MakeSeries(IReadOnlyList<decimal> slopeValues) =>
        slopeValues
            .Select((v, i) => new RsiConcavityCalculator.RsiConcavityInput(FirstDay.AddDays(SlopeStartOffset + i), v))
            .ToList();

    [Fact]
    public void Compute_SevenSlopeReadings_MatchesHandCalculatedConcavitySequence()
    {
        var series = MakeSeries(SlopeValues);

        var results = RsiConcavityCalculator.Compute(series);

        Assert.Equal(ExpectedConcavity.Length, results.Count);
        for (var i = 0; i < ExpectedConcavity.Length; i++)
        {
            Assert.Equal(FirstDay.AddDays(SlopeStartOffset + i + 1), results[i].Date);
            Assert.Equal(ExpectedConcavity[i], results[i].Value);
        }
    }

    [Fact]
    public void Compute_OneSlopeReading_ProducesNoResults()
    {
        var series = MakeSeries(SlopeValues.Take(1).ToArray());

        var results = RsiConcavityCalculator.Compute(series);

        Assert.Empty(results);
    }

    [Fact]
    public void Compute_TwoSlopeReadings_ProducesExactlyOneResult()
    {
        var series = MakeSeries(SlopeValues.Take(2).ToArray()); // 10, 8

        var results = RsiConcavityCalculator.Compute(series);

        var result = Assert.Single(results);
        Assert.Equal(FirstDay.AddDays(SlopeStartOffset + 1), result.Date);
        Assert.Equal(-2m, result.Value); // 8 - 10
    }
}
