namespace Drps.Execution.Alpaca;

public class AlpacaAccountResult
{
    public bool Success { get; set; }

    // Both null whenever Success is false.
    public decimal? BuyingPower { get; set; }
    public decimal? Cash { get; set; }

    public string? ErrorMessage { get; set; }
}
