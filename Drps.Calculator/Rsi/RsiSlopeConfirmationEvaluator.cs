using Drps.Shared.Models;

namespace Drps.Calculator.Rsi;

/// <summary>
/// Pure, deterministic anti-whipsaw confirmation filter for an RsiSlope series - CLAUDE.md's
/// "RsiSlope / RsiConcavity: Design Direction Locked" (2026-07-31) requires a slope's sign to
/// hold for 2+ CONSECUTIVE readings before anything downstream may treat it as a real signal.
/// Same whipsaw-guarding instinct as the NoBuy list (Gate: Seventh Design Decision), applied
/// here to a raw indicator rather than a composite score.
///
/// Operates over an already-ordered, already-gap-filtered sequence of raw slope values (the
/// same sequence that will actually be persisted) - a value's own "streak" position is judged
/// against its immediate predecessor in THIS sequence, not by calendar-day adjacency (gap
/// detection is a separate, earlier concern - see RsiSlopeGapChecker).
/// </summary>
public static class RsiSlopeConfirmationEvaluator
{
    // 2+ consecutive same-sign readings required before a slope is "confirmed" - the exact
    // number the locked design specifies.
    public const int ConfirmationStreakLength = 2;

    /// <summary>
    /// One direction per input value, same order/length as <paramref name="slopeValues"/>. A
    /// zero-valued reading (flat) is neither positive nor negative and always resets any active
    /// streak - it cannot itself be confirmed, and it cannot silently continue whichever streak
    /// preceded it.
    /// </summary>
    public static IReadOnlyList<SlopeConfirmationDirection> Evaluate(IReadOnlyList<decimal> slopeValues)
    {
        var directions = new SlopeConfirmationDirection[slopeValues.Count];

        var streakLength = 0;
        var streakSign = 0;

        for (var i = 0; i < slopeValues.Count; i++)
        {
            var sign = Math.Sign(slopeValues[i]);

            if (sign != 0 && sign == streakSign)
            {
                streakLength++;
            }
            else
            {
                // Sign changed (or flattened to zero) - a single reading in a new direction is
                // never itself confirmed; it must independently accumulate its own streak. A
                // one-off flip is exactly the case this filter exists to NOT flag.
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
