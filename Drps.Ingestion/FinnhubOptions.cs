namespace Drps.Ingestion;

public class FinnhubOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://finnhub.io/api/v1";
}
