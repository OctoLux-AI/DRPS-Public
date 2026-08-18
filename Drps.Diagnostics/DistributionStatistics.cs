namespace Drps.Diagnostics;

// Ratio values are computed and carried as double, not decimal, throughout this file -
// deliberate, and distinct from the Data Type Discipline table's decimal(18,4) rule elsewhere
// in this codebase. That rule governs persisted financial data (money/price/ratios stored in
// the app's own database, where binary rounding could silently corrupt a stored value over
// repeated writes). This is a one-shot, non-persisted diagnostic computation - sqrt/pow are
// needed for standard deviation and skewness, decimal has no such operators, and nothing here
// is ever written back into a production table.
public sealed class DistributionStatistics
{
    public required int Count { get; init; }
    public required double Mean { get; init; }
    public required double Median { get; init; }
    public required double StandardDeviation { get; init; }
    public required double Skewness { get; init; }

    // Key = percentile (10, 25, 50, 75, 90, 95, 99).
    public required IReadOnlyDictionary<int, double> Percentiles { get; init; }

    public required double Min { get; init; }
    public required DateOnly MinDate { get; init; }
    public required double Max { get; init; }
    public required DateOnly MaxDate { get; init; }

    private static readonly int[] ReportedPercentiles = [10, 25, 50, 75, 90, 95, 99];

    // Population statistics (N divisor, not N-1/Bessel's correction) - deliberate: this is a
    // full historical census of every trading day this ratio has ever existed for, not a
    // sample drawn from some larger unobserved population, so a sample-correction would be
    // inventing sampling uncertainty that doesn't apply here.
    public static DistributionStatistics Compute(IReadOnlyList<(DateOnly Date, double Value)> series)
    {
        if (series.Count == 0)
            throw new InvalidOperationException("Cannot compute distribution statistics over an empty series.");

        var ordered = series.OrderBy(p => p.Value).ToList();
        var n = ordered.Count;

        var mean = ordered.Average(p => p.Value);
        var median = Percentile(ordered, 50);

        var variance = ordered.Sum(p => (p.Value - mean) * (p.Value - mean)) / n;
        var stdDev = Math.Sqrt(variance);

        // Population (Fisher-Pearson g1) skewness - third standardized moment. Zero when
        // stdDev is zero (a degenerate, constant series) rather than dividing by zero.
        var skewness = stdDev == 0
            ? 0.0
            : ordered.Sum(p => Math.Pow((p.Value - mean) / stdDev, 3)) / n;

        var percentiles = ReportedPercentiles.ToDictionary(p => p, p => Percentile(ordered, p));

        var minPoint = series.MinBy(p => p.Value);
        var maxPoint = series.MaxBy(p => p.Value);

        return new DistributionStatistics
        {
            Count = n,
            Mean = mean,
            Median = median,
            StandardDeviation = stdDev,
            Skewness = skewness,
            Percentiles = percentiles,
            Min = minPoint.Value,
            MinDate = minPoint.Date,
            Max = maxPoint.Value,
            MaxDate = maxPoint.Date
        };
    }

    // Linear interpolation between closest ranks (the "R-7"/Excel PERCENTILE.INC method) - the
    // most common default across statistics packages, chosen specifically so this report is a
    // reproducibility baseline against a well-known method, not an idiosyncratic one.
    private static double Percentile(List<(DateOnly Date, double Value)> sortedAscending, int percentile)
    {
        if (sortedAscending.Count == 1) return sortedAscending[0].Value;

        var rank = percentile / 100.0 * (sortedAscending.Count - 1);
        var lowerIndex = (int)Math.Floor(rank);
        var upperIndex = (int)Math.Ceiling(rank);
        var fraction = rank - lowerIndex;

        if (lowerIndex == upperIndex) return sortedAscending[lowerIndex].Value;

        var lowerValue = sortedAscending[lowerIndex].Value;
        var upperValue = sortedAscending[upperIndex].Value;
        return lowerValue + (upperValue - lowerValue) * fraction;
    }
}
