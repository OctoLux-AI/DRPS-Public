using Drps.Shared.Models;

namespace Drps.Execution.Firing;

public class FillConfirmationResult
{
    public required FillConfirmationOutcome Outcome { get; init; }

    // Human-readable explanation - null only when Outcome is Recorded.
    public string? Reason { get; init; }

    // Populated only for an open whose Outcome is Recorded - ClosePositionAsync has no return
    // value, so a close's Recorded result never populates this.
    public Position? Position { get; init; }

    // Populated whenever a terminal Alpaca order response was actually observed (Recorded,
    // NoFillRecorded's zero case still leaves these null, AnomalousFillData) - null for
    // TimedOut, since no usable terminal response was ever obtained.
    public decimal? FilledQuantity { get; init; }
    public decimal? FilledAveragePrice { get; init; }
}
