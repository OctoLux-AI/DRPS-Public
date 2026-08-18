namespace Drps.Execution.Alpaca;

public class AlpacaQuoteResult
{
    public bool Success { get; set; }

    // Both null whenever Success is false.
    public decimal? Ask { get; set; }
    public decimal? Bid { get; set; }

    public string? ErrorMessage { get; set; }
}
