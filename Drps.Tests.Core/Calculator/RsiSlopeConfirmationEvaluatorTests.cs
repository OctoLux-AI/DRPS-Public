using Drps.Calculator.Rsi;
using Drps.Shared.Models;

namespace Drps.Tests.Calculator;

public class RsiSlopeConfirmationEvaluatorTests
{
    [Fact]
    public void Evaluate_HandCalculatedSlopeSequence_MatchesExpectedConfirmationPattern()
    {
        // Same 10, 8, 3, -1, 2, 9, 9 sequence hand-verified in RsiSlopeCalculatorTests.
        // Signs: +, +, +, -, +, +, +
        var slopeValues = new[] { 10m, 8m, 3m, -1m, 2m, 9m, 9m };

        var directions = RsiSlopeConfirmationEvaluator.Evaluate(slopeValues);

        Assert.Equal(new[]
        {
            SlopeConfirmationDirection.Unconfirmed,      // 10  - first positive reading, streak = 1
            SlopeConfirmationDirection.ConfirmedPositive, // 8  - second consecutive positive, streak = 2
            SlopeConfirmationDirection.ConfirmedPositive, // 3  - third consecutive positive
            SlopeConfirmationDirection.Unconfirmed,      // -1 - sign flips; a single flip must NOT confirm negative
            SlopeConfirmationDirection.Unconfirmed,      // 2  - flips back to positive; single reading, not yet confirmed
            SlopeConfirmationDirection.ConfirmedPositive, // 9  - second consecutive positive since the flip-back
            SlopeConfirmationDirection.ConfirmedPositive  // 9  - third consecutive positive
        }, directions);
    }

    [Fact]
    public void Evaluate_SingleSignFlipAfterConfirmedStreak_DoesNotFlipToConfirmedOppositeDirection()
    {
        // Isolates the requirement stated explicitly in the task: "a slope that flips sign
        // once should NOT flip the confirmed flag."
        var slopeValues = new[] { 5m, 5m, -5m };

        var directions = RsiSlopeConfirmationEvaluator.Evaluate(slopeValues);

        Assert.Equal(SlopeConfirmationDirection.ConfirmedPositive, directions[1]);
        Assert.Equal(SlopeConfirmationDirection.Unconfirmed, directions[2]);
    }

    [Fact]
    public void Evaluate_SignHoldsForTwoConsecutiveReadings_BecomesConfirmedOnTheSecond()
    {
        var slopeValues = new[] { -3m, -3m };

        var directions = RsiSlopeConfirmationEvaluator.Evaluate(slopeValues);

        Assert.Equal(SlopeConfirmationDirection.Unconfirmed, directions[0]);
        Assert.Equal(SlopeConfirmationDirection.ConfirmedNegative, directions[1]);
    }

    [Fact]
    public void Evaluate_ZeroReading_IsUnconfirmedAndResetsAnActiveStreak()
    {
        var slopeValues = new[] { 4m, 4m, 0m, 4m };

        var directions = RsiSlopeConfirmationEvaluator.Evaluate(slopeValues);

        Assert.Equal(SlopeConfirmationDirection.ConfirmedPositive, directions[1]); // confirmed before the zero
        Assert.Equal(SlopeConfirmationDirection.Unconfirmed, directions[2]);       // zero itself
        Assert.Equal(SlopeConfirmationDirection.Unconfirmed, directions[3]);       // streak had to restart from 1
    }

    [Fact]
    public void Evaluate_EmptyInput_ReturnsEmpty()
    {
        var directions = RsiSlopeConfirmationEvaluator.Evaluate(Array.Empty<decimal>());

        Assert.Empty(directions);
    }
}
