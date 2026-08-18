namespace Drps.Calculator;

// Bound from configuration section "Alpaca" via IOptions<AlpacaOptions> - the same config
// section Drps.Ingestion's AlpacaFeeder reads (same user-secrets/shared-APIKeys.json
// entries), reusing the credential pattern without referencing Drps.Ingestion directly
// (Calculator may only reference Drps.Shared). Own copy, same shape as
// Drps.Ingestion/AlpacaOptions.cs - see Drps.Calculator/Logging/FileLoggerProvider.cs for
// the precedent of duplicating a small Ingestion-side pattern rather than editing Ingestion.
public class AlpacaOptions
{
    public string KeyId { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;

    // /v2/calendar is a Trading API endpoint, not the Market Data API AlpacaFeeder calls -
    // paper trading base URL by default, same default Drps.Ingestion's AlpacaOptions uses.
    public string BaseUrl { get; set; } = "https://paper-api.alpaca.markets";
}
