using Drps.Shared.Models;

namespace Drps.Tests.Calculator;

/// <summary>
/// Pure entity-level tests, independent of the computation-service pipeline: confirms
/// VerificationScopeLimited exists and defaults to true purely from the C# property
/// initializer on RsiIndicator/AtrIndicator, without needing a DbContext at all.
/// RsiComputationServiceTests/AtrComputationServiceTests separately confirm it also holds
/// true end-to-end, on every row a real computation run persists.
/// </summary>
public class VerificationScopeLimitedTests
{
    [Fact]
    public void RsiIndicator_ConstructedWithoutExplicitlySettingTheField_DefaultsToTrue()
    {
        var indicator = new RsiIndicator
        {
            Symbol = "AAPL",
            BarDate = new DateOnly(2026, 1, 1),
            Period = 14,
            Value = 50m,
            CalculationVersion = 1,
            ComputedAt = DateTimeOffset.UtcNow
        };

        Assert.True(indicator.VerificationScopeLimited);
    }

    [Fact]
    public void AtrIndicator_ConstructedWithoutExplicitlySettingTheField_DefaultsToTrue()
    {
        var indicator = new AtrIndicator
        {
            Symbol = "AAPL",
            BarDate = new DateOnly(2026, 1, 1),
            Period = 14,
            Value = 2m,
            CalculationVersion = 1,
            ComputedAt = DateTimeOffset.UtcNow
        };

        Assert.True(indicator.VerificationScopeLimited);
    }
}
