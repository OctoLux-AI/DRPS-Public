using Drps.Calculator.Rsi;
using Drps.Shared.Models;

namespace Drps.Tests.Calculator;

public class RsiConcavityConfirmationEvaluatorTests
{
    [Fact]
    public void Evaluate_HandCalculatedConcavitySequence_MatchesExpectedConfirmationPattern()
    {
        // Same -2, -5, -4, 3, 7, 0 sequence hand-verified in RsiConcavityCalculatorTests.
        // Signs: -, -, -, +, +, 0
        var concavityValues = new[] { -2m, -5m, -4m, 3m, 7m, 0m };

        var directions = RsiConcavityConfirmationEvaluator.Evaluate(concavityValues);

        Assert.Equal(new[]
        {
            SlopeConfirmationDirection.Unconfirmed,      // -2 - streak = 1
            SlopeConfirmationDirection.Unconfirmed,      // -5 - streak = 2, still below the 3-reading floor
            SlopeConfirmationDirection.ConfirmedNegative, // -4 - streak = 3, now confirmed
            SlopeConfirmationDirection.Unconfirmed,      // 3  - sign flips, streak = 1
            SlopeConfirmationDirection.Unconfirmed,      // 7  - streak = 2, still below the floor
            SlopeConfirmationDirection.Unconfirmed       // 0  - flat, resets the streak entirely
        }, directions);
    }

    [Fact]
    public void Evaluate_TwoConsecutiveSameSignReadings_IsNotEnoughToConfirm()
    {
        // The exact behavior that distinguishes RsiConcavityConfirmationEvaluator from
        // RsiSlopeConfirmationEvaluator: 2 consecutive readings confirm a slope, but must NOT
        // confirm a concavity, which requires a 3rd.
        var concavityValues = new[] { 6m, 6m };

        var directions = RsiConcavityConfirmationEvaluator.Evaluate(concavityValues);

        Assert.All(directions, d => Assert.Equal(SlopeConfirmationDirection.Unconfirmed, d));
    }

    [Fact]
    public void Evaluate_ThreeConsecutiveSameSignReadings_ConfirmsOnTheThird()
    {
        var concavityValues = new[] { 6m, 6m, 6m };

        var directions = RsiConcavityConfirmationEvaluator.Evaluate(concavityValues);

        Assert.Equal(SlopeConfirmationDirection.Unconfirmed, directions[0]);
        Assert.Equal(SlopeConfirmationDirection.Unconfirmed, directions[1]);
        Assert.Equal(SlopeConfirmationDirection.ConfirmedPositive, directions[2]);
    }

    [Fact]
    public void Evaluate_SingleSignFlipAfterConfirmedStreak_DoesNotFlipToConfirmedOppositeDirection()
    {
        var concavityValues = new[] { 5m, 5m, 5m, -5m };

        var directions = RsiConcavityConfirmationEvaluator.Evaluate(concavityValues);

        Assert.Equal(SlopeConfirmationDirection.ConfirmedPositive, directions[2]);
        Assert.Equal(SlopeConfirmationDirection.Unconfirmed, directions[3]);
    }
}
