using Drps.Adjuster.Scoring;
using Drps.Shared.Models;

namespace Drps.Tests.Adjuster;

public class AdjusterParametersValidatorTests
{
    // Today's real shipped values (same fixture AdjusterParameters' own property
    // initializers hold) - a genuinely valid baseline every broken-field test mutates
    // exactly one property away from.
    private static AdjusterParameters CreateValid() => new()
    {
        TierOneFloor = 0.85m,
        TierOneCeiling = 0.89m,
        TierTwoCeiling = 0.93m,
        TierOneBaseRate = 0.03m,
        TierTwoBaseRate = 0.04m,
        TierThreeBaseRate = 0.05m,
        SectorCapPercent = 0.30m,
        BaseReservePercent = 0.25m,
        ReserveStepPercent = 0.10m,
        ReserveMilestoneOne = 10000m,
        ReserveMilestoneTwo = 100000m
    };

    [Fact]
    public void Validate_FullyValidFixture_ReturnsNoViolations()
    {
        var violations = AdjusterParametersValidator.Validate(CreateValid());

        Assert.Empty(violations);
        Assert.True(AdjusterParametersValidator.IsValid(CreateValid()));
    }

    [Theory]
    [InlineData(0.89, 0.85, 0.93)] // TierOneFloor > TierOneCeiling
    [InlineData(0.85, 0.93, 0.89)] // TierOneCeiling > TierTwoCeiling
    [InlineData(0.85, 0.85, 0.93)] // TierOneFloor == TierOneCeiling, not strictly increasing
    public void Validate_TierBoundsNotStrictlyIncreasing_ReturnsViolation(decimal floor, decimal ceiling1, decimal ceiling2)
    {
        var parameters = CreateValid();
        parameters.TierOneFloor = floor;
        parameters.TierOneCeiling = ceiling1;
        parameters.TierTwoCeiling = ceiling2;

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("TierOneFloor < TierOneCeiling < TierTwoCeiling"));
    }

    [Theory]
    [InlineData(0.04, 0.03, 0.05)] // TierOneBaseRate > TierTwoBaseRate
    [InlineData(0.03, 0.05, 0.04)] // TierTwoBaseRate > TierThreeBaseRate
    [InlineData(0.03, 0.03, 0.05)] // TierOneBaseRate == TierTwoBaseRate, not strictly increasing
    public void Validate_BaseRatesNotStrictlyIncreasing_ReturnsViolation(decimal tierOne, decimal tierTwo, decimal tierThree)
    {
        var parameters = CreateValid();
        parameters.TierOneBaseRate = tierOne;
        parameters.TierTwoBaseRate = tierTwo;
        parameters.TierThreeBaseRate = tierThree;

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("TierOneBaseRate < TierTwoBaseRate < TierThreeBaseRate"));
    }

    [Fact]
    public void Validate_SectorCapPercentIsZero_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.SectorCapPercent = 0m;

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("SectorCapPercent must be strictly between 0 and 1"));
    }

    [Fact]
    public void Validate_SectorCapPercentIsOne_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.SectorCapPercent = 1m;

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("SectorCapPercent must be strictly between 0 and 1"));
    }

    [Fact]
    public void Validate_BaseReservePercentIsZero_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.BaseReservePercent = 0m;

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("BaseReservePercent must be strictly between 0 and 1"));
    }

    [Fact]
    public void Validate_BaseReservePercentIsOne_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.BaseReservePercent = 1m;

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("BaseReservePercent must be strictly between 0 and 1"));
    }

    [Fact]
    public void Validate_ReserveStepPercentIsZero_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.ReserveStepPercent = 0m;

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("ReserveStepPercent must be > 0"));
    }

    [Fact]
    public void Validate_ReserveStepPercentIsNegative_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.ReserveStepPercent = -0.05m;

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("ReserveStepPercent must be > 0"));
    }

    [Fact]
    public void Validate_ReserveScheduleCeilingExactlyOne_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.BaseReservePercent = 0.30m;
        parameters.ReserveStepPercent = 0.35m; // 0.30 + 2*0.35 = 1.00, not < 1

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("BaseReservePercent + (2 * ReserveStepPercent) must be < 1"));
    }

    [Fact]
    public void Validate_ReserveScheduleCeilingExceedsOne_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.BaseReservePercent = 0.50m;
        parameters.ReserveStepPercent = 0.30m; // 0.50 + 2*0.30 = 1.10, > 1

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("BaseReservePercent + (2 * ReserveStepPercent) must be < 1"));
    }

    [Fact]
    public void Validate_ReserveMilestoneOneNotLessThanMilestoneTwo_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.ReserveMilestoneOne = 100000m;
        parameters.ReserveMilestoneTwo = 100000m;

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("ReserveMilestoneOne < ReserveMilestoneTwo"));
    }

    [Fact]
    public void Validate_ReserveMilestoneOneGreaterThanMilestoneTwo_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.ReserveMilestoneOne = 200000m;
        parameters.ReserveMilestoneTwo = 100000m;

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("ReserveMilestoneOne < ReserveMilestoneTwo"));
    }

    [Fact]
    public void Validate_ReserveMilestoneOneIsZero_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.ReserveMilestoneOne = 0m;

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("ReserveMilestoneOne must be > 0"));
    }

    [Fact]
    public void Validate_ReserveMilestoneTwoIsZero_ReturnsViolation()
    {
        var parameters = CreateValid();
        // ReserveMilestoneOne set below zero so the ordering rule still holds (-10000 < 0) -
        // isolates this case from the ordering violation, though ReserveMilestoneOne's own
        // ">0" rule unavoidably fires too (a milestone pair satisfying strict ordering while
        // MilestoneTwo is 0 can only do so with a negative MilestoneOne).
        parameters.ReserveMilestoneOne = -10000m;
        parameters.ReserveMilestoneTwo = 0m;

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("ReserveMilestoneTwo must be > 0"));
    }

    [Fact]
    public void Validate_ConcurrentPositionDisplacementMarginPercentIsNegative_ReturnsViolation()
    {
        var parameters = CreateValid();
        parameters.ConcurrentPositionDisplacementMarginPercent = -0.01m;

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.Contains(violations, v => v.Contains("ConcurrentPositionDisplacementMarginPercent must be >= 0"));
    }

    [Fact]
    public void Validate_ConcurrentPositionDisplacementMarginPercentIsZero_NoViolation()
    {
        // Zero is a legitimate (if degenerate - "any strictly higher score displaces")
        // configuration, not an error - only negative values are invalid.
        var parameters = CreateValid();
        parameters.ConcurrentPositionDisplacementMarginPercent = 0m;

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.DoesNotContain(violations, v => v.Contains("ConcurrentPositionDisplacementMarginPercent"));
    }

    [Fact]
    public void Validate_AllZeroRow_ReturnsMultipleDistinctViolations()
    {
        // The exact shape the migration's SQL-level column defaults would produce if a row
        // were ever inserted by something other than a deliberate seeder - the real scenario
        // this validator exists to catch. NOT the same as `new AdjusterParameters()`, whose C#
        // property initializers already hold today's real shipped values - explicitly zeroed
        // out here to simulate a raw INSERT that left every threshold column at its SQL
        // default instead of going through a seeder.
        var parameters = new AdjusterParameters
        {
            TierOneFloor = 0m,
            TierOneCeiling = 0m,
            TierTwoCeiling = 0m,
            TierOneBaseRate = 0m,
            TierTwoBaseRate = 0m,
            TierThreeBaseRate = 0m,
            SectorCapPercent = 0m,
            BaseReservePercent = 0m,
            ReserveStepPercent = 0m,
            ReserveMilestoneOne = 0m,
            ReserveMilestoneTwo = 0m
        };

        var violations = AdjusterParametersValidator.Validate(parameters);

        Assert.NotEmpty(violations);
        Assert.True(violations.Count > 1, "an all-zero row should fail multiple independent rules at once");
    }
}
