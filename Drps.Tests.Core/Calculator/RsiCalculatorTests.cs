using Drps.Calculator.Rsi;

namespace Drps.Tests.Calculator;

public class RsiCalculatorTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    // Hand-calculated reference sequence (not a published dataset - constructed and
    // verified by hand here so the expected values are independently auditable). 16 closes,
    // deltas for i=1..14 deliberately engineered to a clean 3:1 gain:loss ratio for the
    // first (seed) value:
    //   +2,-1,+2,+1,-2,+3,+2,-1,+2,+2,-1,+2,+2,-1  -> sum gains=18, sum losses=6
    //   avgGain = 18/14, avgLoss = 6/14, RS = 18/6 = 3 exactly
    //   RSI = 100 - 100/(1+3) = 75
    // 16th close (114, delta +2 from 112) tests the Wilder-smoothing recurrence for a
    // second value:
    //   avgGain' = (18/14*13 + 2)/14 = 262/196,  avgLoss' = (6/14*13 + 0)/14 = 78/196
    //   RS' = 262/78 = 131/39 ~= 3.358974358974359
    //   RSI' = 100 - 100/(1+131/39) = 100 - 3900/170 = 1310/17 ~= 77.05882352941176...
    private static readonly decimal[] Closes =
    {
        100m, 102m, 101m, 103m, 104m, 102m, 105m, 107m, 106m, 108m, 110m, 109m, 111m, 113m, 112m, 114m
    };

    private static List<RsiCalculator.RsiBarInput> MakeBars(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new RsiCalculator.RsiBarInput(FirstDay.AddDays(i), Closes[i]))
            .ToList();

    [Fact]
    public void Compute_FewerThanFifteenBars_ReturnsNoResults()
    {
        var bars = MakeBars(14); // Period + 1 = 15 required for the first value

        var results = RsiCalculator.Compute(bars);

        Assert.Empty(results);
    }

    [Fact]
    public void Compute_ExactlyFifteenBars_EmitsOneSeedValueMatchingHandCalculation()
    {
        var bars = MakeBars(15);

        var results = RsiCalculator.Compute(bars);

        var result = Assert.Single(results);
        Assert.Equal(14, result.Period);
        Assert.Equal(bars[14].Date, result.Date);
        Assert.Equal(75m, result.Value); // hand-calculated: 100 - 100/(1+3) = 75, exact
    }

    [Fact]
    public void Compute_SixteenBars_SecondValueMatchesHandCalculatedWildersSmoothing()
    {
        var bars = MakeBars(16);

        var results = RsiCalculator.Compute(bars);

        Assert.Equal(2, results.Count);
        var second = results[1];
        Assert.Equal(bars[15].Date, second.Date);
        // hand-calculated: 1310/17 = 77.058823529411764705882... - compared to 4 decimal
        // places since the true value is a non-terminating repeating decimal.
        Assert.Equal(77.0588m, second.Value, 4);
    }

    [Fact]
    public void Compute_AllGainsNoLosses_ReturnsExactlyOneHundred()
    {
        var closes = Enumerable.Range(0, 15).Select(i => 100m + i).ToArray(); // strictly increasing, every delta a gain
        var bars = closes.Select((c, i) => new RsiCalculator.RsiBarInput(FirstDay.AddDays(i), c)).ToList();

        var results = RsiCalculator.Compute(bars);

        var result = Assert.Single(results);
        Assert.Equal(100m, result.Value);
    }

    [Fact]
    public void Compute_AllLossesNoGains_ReturnsExactlyZero()
    {
        var closes = Enumerable.Range(0, 15).Select(i => 200m - i).ToArray(); // strictly decreasing, every delta a loss
        var bars = closes.Select((c, i) => new RsiCalculator.RsiBarInput(FirstDay.AddDays(i), c)).ToList();

        var results = RsiCalculator.Compute(bars);

        var result = Assert.Single(results);
        Assert.Equal(0m, result.Value);
    }

    [Fact]
    public void Compute_FlatPrices_ReturnsOneHundred()
    {
        // No losses at all (all deltas zero) hits the same avgLoss == 0 guard as the
        // all-gains case - documents that a flat, unmoving price is also treated as
        // maximally "overbought" by the formula's own zero-loss special case, not a bug.
        var bars = Enumerable.Range(0, 15).Select(i => new RsiCalculator.RsiBarInput(FirstDay.AddDays(i), 100m)).ToList();

        var results = RsiCalculator.Compute(bars);

        var result = Assert.Single(results);
        Assert.Equal(100m, result.Value);
    }
}
