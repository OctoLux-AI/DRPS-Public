namespace Drps.Ingestion;

// Bound from configuration section "Alpaca" via IOptions<AlpacaOptions>. Property names match
// the shared credentials file (C:\Users\kent\.APIKeys\shared-APIKeys.json)'s Alpaca:KeyId/
// Alpaca:SecretKey keys - that file is the single source of truth across projects, not
// something to rename to match this class. Real credentials must never be committed to
// appsettings.json — set them per-developer with:
//   dotnet user-secrets set "Alpaca:KeyId" "..."
//   dotnet user-secrets set "Alpaca:SecretKey" "..."
public class AlpacaOptions
{
    public string KeyId { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://paper-api.alpaca.markets";
    public string DataBaseUrl { get; set; } = "https://data.alpaca.markets";
}
