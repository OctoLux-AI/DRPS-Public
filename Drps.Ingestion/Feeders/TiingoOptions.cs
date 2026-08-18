namespace Drps.Ingestion.Feeders;

// Bound from configuration section "Tiingo" via IOptions<TiingoOptions>. Real credentials
// must never be committed to appsettings.json — set them per-developer with:
//   dotnet user-secrets set "Tiingo:ApiKey" "..."
public class TiingoOptions
{
    public string ApiKey { get; set; } = string.Empty;
}
