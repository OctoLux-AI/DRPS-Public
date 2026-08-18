namespace Drps.Ingestion.Feeders;

public class AccountFetchResult
{
    public bool Success { get; set; }

    // Alpaca's own current available buying power - null whenever Success is false.
    public decimal? BuyingPower { get; set; }

    public string? ErrorMessage { get; set; }
}
