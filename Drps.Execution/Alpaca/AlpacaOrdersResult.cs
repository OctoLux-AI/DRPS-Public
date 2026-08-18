namespace Drps.Execution.Alpaca;

public class AlpacaOrdersResult
{
    public bool Success { get; set; }

    // Empty (not null) whenever Success is true and no closed orders exist yet for this symbol
    // - a real, valid outcome, distinct from Success = false.
    public IReadOnlyList<AlpacaOrder> Orders { get; set; } = Array.Empty<AlpacaOrder>();

    public string? ErrorMessage { get; set; }
}
