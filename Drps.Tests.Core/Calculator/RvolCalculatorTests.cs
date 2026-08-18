using Drps.Calculator.Rvol;

namespace Drps.Tests.Calculator;

public class RvolCalculatorTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    private static List<RvolCalculator.RvolBarInput> MakeBars(IEnumerable<long> volumes) =>
        volumes
            .Select((v, i) => new RvolCalculator.RvolBarInput(FirstDay.AddDays(i), v))
            .ToList();

    [Fact]
    public void Compute_FewerThanTwentyOneBars_ReturnsNoResults()
    {
        var bars = MakeBars(Enumerable.Repeat(1000L, 20)); // WindowSize is 21 (20 baseline + current)

        var results = RvolCalculator.Compute(bars);

        Assert.Empty(results);
    }

    [Fact]
    public void Compute_KnownVolumeSequence_MatchesHandCalculatedRvol()
    {
        // 20 baseline bars of constant volume 1000, 21st bar spikes to 2500.
        // baseline average = 1000 (constant), RVOL = 2500 / 1000 = 2.5 exactly.
        var volumes = Enumerable.Repeat(1000L, 20).Append(2500L);
        var bars = MakeBars(volumes);

        var results = RvolCalculator.Compute(bars);

        var result = Assert.Single(results);
        Assert.Equal(bars[20].Date, result.Date);
        Assert.Equal(2.5m, result.Value);
    }

    [Fact]
    public void Compute_CurrentBarVolume_IsExcludedFromItsOwnBaselineAverage()
    {
        // If the current (21st) bar's spike were wrongly folded into its own baseline
        // average, the result would be materially different from 2.5 (it would be
        // (20*1000 + 2500)/21 = ~1071.43 as the baseline, giving RVOL ~2.33, not 2.5). This
        // test exists as an explicit, named guard against that specific bug, even though
        // Compute_KnownVolumeSequence_MatchesHandCalculatedRvol already exercises the same
        // math.
        var volumes = Enumerable.Repeat(1000L, 20).Append(2500L);
        var bars = MakeBars(volumes);

        var result = Assert.Single(RvolCalculator.Compute(bars));

        Assert.NotEqual(2500m / (22500m / 21m), result.Value); // the wrong-formula answer
        Assert.Equal(2.5m, result.Value); // the correct answer
    }

    [Fact]
    public void Compute_RollingBaselineShiftsCorrectlyForASecondResult()
    {
        // 21 bars of constant volume 1000 (giving a first RVOL of exactly 1.0, proving the
        // baseline correctly excludes the current bar - if it were included the first
        // result also wouldn't be exactly 1.0 for a constant series, though that
        // coincidentally cancels out; the real proof is the spike test above), then a 22nd
        // bar spikes to 3000. Baseline for the 22nd bar is bars[1..20] (still all 1000s),
        // so RVOL = 3000 / 1000 = 3.0 exactly - confirms the rolling window correctly
        // dropped bars[0] and picked up bars[20] without disturbing the average.
        var volumes = Enumerable.Repeat(1000L, 21).Append(3000L);
        var bars = MakeBars(volumes);

        var results = RvolCalculator.Compute(bars);

        Assert.Equal(2, results.Count);
        Assert.Equal(bars[20].Date, results[0].Date);
        Assert.Equal(1.0m, results[0].Value);
        Assert.Equal(bars[21].Date, results[1].Date);
        Assert.Equal(3.0m, results[1].Value);
    }

    [Fact]
    public void Compute_ZeroVolumeBaseline_SkipsThatResultRatherThanDividingByZero()
    {
        // All 20 baseline bars have zero volume (e.g. a run of halted days) - dividing by a
        // zero baseline would either throw or produce a misleading sentinel, so this result
        // is skipped entirely rather than computed.
        var volumes = Enumerable.Repeat(0L, 20).Append(500L);
        var bars = MakeBars(volumes);

        var results = RvolCalculator.Compute(bars);

        Assert.Empty(results);
    }
}
