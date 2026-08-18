namespace Drps.Ingestion;

public class MarketauxOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.marketaux.com/v1";
}
