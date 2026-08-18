namespace Drps.Shared.Models;

/// <summary>
/// Confirmation-filtered sign of a first- or second-discrete-difference indicator
/// (RsiSlopeIndicator/RsiConcavityIndicator) - a single reading's raw sign is never itself a
/// signal (CLAUDE.md, "RsiSlope / RsiConcavity: Design Direction Locked", 2026-07-31); this is
/// the result of requiring the sign to hold for a minimum consecutive-reading streak before it
/// counts as "confirmed." See RsiSlopeConfirmationEvaluator/RsiConcavityConfirmationEvaluator
/// for the actual streak-length rules (deliberately different lengths for the two indicators).
/// </summary>
public enum SlopeConfirmationDirection
{
    Unconfirmed = 0,
    ConfirmedPositive = 1,
    ConfirmedNegative = 2
}
