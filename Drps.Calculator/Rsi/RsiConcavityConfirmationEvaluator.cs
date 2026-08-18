using Drps.Shared.Models;

namespace Drps.Calculator.Rsi;

/// <summary>
/// Pure, deterministic anti-whipsaw confirmation filter for an RsiConcavity series - same
/// mechanism as RsiSlopeConfirmationEvaluator, but with its OWN, longer/stricter streak length.
///
/// Per CLAUDE.md's "RsiSlope / RsiConcavity: Design Direction Locked" (2026-07-31): concavity
/// differences an already-differenced value (RsiSlope itself is already a first difference of
/// RSI), which compounds noise - a genuine second-derivative signal is a noisier quantity than
/// the first-derivative slope it's built from, so this filter deliberately requires one
/// additional confirmed reading beyond RsiSlope's 2-reading rule (3, not 2) as a conservative
/// buffer against exactly that extra noise. Not reusing RsiSlopeConfirmationEvaluator's filter
/// at face value, per the locked design's own instruction.
/// </summary>
public static class RsiConcavityConfirmationEvaluator
{
    // One longer than RsiSlopeConfirmationEvaluator.ConfirmationStreakLength (2) - see this
    // class's own doc comment for why.
    public const int ConfirmationStreakLength = 3;

    /// <summary>
    /// One direction per input value, same order/length as <paramref name="concavityValues"/>.
    /// Same zero-resets-the-streak semantics as RsiSlopeConfirmationEvaluator.
    /// </summary>
    public static IReadOnlyList<SlopeConfirmationDirection> Evaluate(IReadOnlyList<decimal> concavityValues)
    {
        var directions = new SlopeConfirmationDirection[concavityValues.Count];

        var streakLength = 0;
        var streakSign = 0;

        for (var i = 0; i < concavityValues.Count; i++)
        {
            var sign = Math.Sign(concavityValues[i]);

            if (sign != 0 && sign == streakSign)
            {
                streakLength++;
            }
            else
            {
                streakLength = sign == 0 ? 0 : 1;
                streakSign = sign;
            }

            directions[i] = streakLength >= ConfirmationStreakLength
                ? (streakSign > 0 ? SlopeConfirmationDirection.ConfirmedPositive : SlopeConfirmationDirection.ConfirmedNegative)
                : SlopeConfirmationDirection.Unconfirmed;
        }

        return directions;
    }
}
