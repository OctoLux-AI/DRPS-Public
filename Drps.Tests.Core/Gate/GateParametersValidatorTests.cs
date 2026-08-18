using Drps.Gate.Scoring;
using Drps.Shared.Models;

namespace Drps.Tests.Gate;

public class GateParametersValidatorTests
{
    // Today's real shipped values (same fixture GateParametersSeeder inserts) - a genuinely
    // valid baseline every broken-field test mutates exactly one property away from.
    private static GateParameters CreateValid() => new()
    {
        RsiLowerBound = 50m,
        RsiPeak = 60m,
        RsiUpperBound = 70m,
        RsiFloorQuality = 0.75m,
        RvolFloorMultiple = 1.5m,
        RvolCeilingMultiple = 3.0m,
        RvolFullWeight = 0.30m,
        RvolHalfWeight = 0.15m,
        RsiCompositeWeight = 0.70m,
        BuyThreshold = 0.85m,
        WatchThreshold = 0.75m,
        ExitThreshold = 0.70m,
        NoBuySessionCount = 2
    };

    [Fact]
    public void Validate_FullyValidFixture_ReturnsNoViolations()
    {
        var violations = GateParametersValidator.Validate(CreateValid());

        Assert.Empty(violations);
        Assert.True(GateParametersValidator.IsValid(CreateValid()));
    }

    [Theory]
    [InlineData(60, 50, 70)] // RsiLowerBound > RsiPeak
    [InlineData(50, 70, 60)] // RsiPeak > RsiUpperBound
    [InlineData(50, 50, 70)] // RsiLowerBound == RsiPeak, not strictly increasing
    public void Validate_RsiBoundsNotStrictlyIncreasing_ReturnsViolation(decimal lower, decimal peak, decimal upper)
    {
        var parameters = CreateValid();
        parameters.RsiLowerBound = lower;
        parameters.RsiPeak = peak;
        parameters.RsiUpperBound = upper;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("RsiLowerBound < RsiPeak < RsiUpperBound"));
    }

    [Fact]
    public void Validate_RsiFloorQualityIsZero_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.RsiFloorQuality = 0m;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("RsiFloorQuality"));
    }

    [Fact]
    public void Validate_RsiFloorQualityAboveOne_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.RsiFloorQuality = 1.1m;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("RsiFloorQuality"));
    }

    [Fact]
    public void Validate_RsiFloorQualityExactlyOne_IsValid()
    {
        // Maximally strict, but a legitimate configuration, not a defeated gate.
        var parameters = CreateValid();
        parameters.RsiFloorQuality = 1.0m;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.DoesNotContain(violations, v => v.Contains("RsiFloorQuality"));
    }

    [Fact]
    public void Validate_RsiPeakIsZero_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.RsiLowerBound = -10m;
        parameters.RsiPeak = 0m;
        parameters.RsiUpperBound = 10m;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("RsiPeak must be > 0"));
    }

    [Fact]
    public void Validate_RvolFloorNotLessThanCeiling_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.RvolFloorMultiple = 3.0m;
        parameters.RvolCeilingMultiple = 3.0m;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("RvolFloorMultiple < RvolCeilingMultiple"));
    }

    [Fact]
    public void Validate_RvolCeilingMultipleIsZero_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.RvolFloorMultiple = -1m;
        parameters.RvolCeilingMultiple = 0m;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("RvolCeilingMultiple must be > 0"));
    }

    [Fact]
    public void Validate_RvolFullWeightIsZero_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.RvolFullWeight = 0m;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("RvolFullWeight and RvolHalfWeight"));
    }

    [Fact]
    public void Validate_RvolHalfWeightIsZero_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.RvolHalfWeight = 0m;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("RvolFullWeight and RvolHalfWeight"));
    }

    [Fact]
    public void Validate_RvolHalfWeightNotLessThanFullWeight_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.RvolFullWeight = 0.30m;
        parameters.RvolHalfWeight = 0.30m;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("RvolHalfWeight must be less than RvolFullWeight"));
    }

    [Fact]
    public void Validate_RsiCompositeWeightIsZero_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.RsiCompositeWeight = 0m;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("RsiCompositeWeight"));
    }

    [Fact]
    public void Validate_RsiCompositeWeightIsOne_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.RsiCompositeWeight = 1m;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("RsiCompositeWeight"));
    }

    [Theory]
    [InlineData(0.85, 0.85, 0.70)] // WatchThreshold == BuyThreshold
    [InlineData(0.85, 0.75, 0.75)] // ExitThreshold == WatchThreshold
    [InlineData(0.70, 0.85, 0.75)] // WatchThreshold > BuyThreshold
    public void Validate_BucketThresholdsNotStrictlyIncreasing_ReturnsViolation(
        decimal buyThreshold, decimal watchThreshold, decimal exitThreshold)
    {
        var parameters = CreateValid();
        parameters.BuyThreshold = buyThreshold;
        parameters.WatchThreshold = watchThreshold;
        parameters.ExitThreshold = exitThreshold;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("ExitThreshold < WatchThreshold < BuyThreshold"));
    }

    [Fact]
    public void Validate_ExitThresholdIsZero_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.ExitThreshold = 0m;
        parameters.WatchThreshold = 0.5m;
        parameters.BuyThreshold = 0.9m;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("ExitThreshold must be > 0"));
    }

    [Fact]
    public void Validate_AllZeroRow_ReturnsMultipleDistinctViolations()
    {
        // The exact shape the migration's SQL-level column defaults would produce if a row
        // were ever inserted by something other than GateParametersSeeder - the real scenario
        // this validator exists to catch. NOT the same as `new GateParameters()`, whose C#
        // property initializers already hold today's real shipped values - explicitly zeroed
        // out here to simulate a raw INSERT that left every threshold column at its SQL
        // default instead of going through the seeder.
        var parameters = new GateParameters
        {
            RsiLowerBound = 0m,
            RsiPeak = 0m,
            RsiUpperBound = 0m,
            RsiFloorQuality = 0m,
            RvolFloorMultiple = 0m,
            RvolCeilingMultiple = 0m,
            RvolFullWeight = 0m,
            RvolHalfWeight = 0m,
            RsiCompositeWeight = 0m,
            BuyThreshold = 0m,
            WatchThreshold = 0m,
            ExitThreshold = 0m,
            NoBuySessionCount = 0
        };

        var violations = GateParametersValidator.Validate(parameters);

        Assert.NotEmpty(violations);
        Assert.True(violations.Count > 1, "an all-zero row should fail multiple independent rules at once");
    }

    [Fact]
    public void Validate_NoBuySessionCountIsZero_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.NoBuySessionCount = 0;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("NoBuySessionCount must be > 0"));
    }

    [Fact]
    public void Validate_NoBuySessionCountIsNegative_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.NoBuySessionCount = -1;

        var violations = GateParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("NoBuySessionCount must be > 0"));
    }
}
