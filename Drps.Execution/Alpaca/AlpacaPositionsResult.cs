namespace Drps.Execution.Alpaca;

public class AlpacaPositionsResult
{
    public bool Success { get; set; }

    // Empty (not null) whenever Success is true and Alpaca genuinely holds zero open
    // positions - a real, valid outcome, distinct from Success = false.
    public IReadOnlyList<AlpacaPosition> Positions { get; set; } = Array.Empty<AlpacaPosition>();

    public string? ErrorMessage { get; set; }
}
