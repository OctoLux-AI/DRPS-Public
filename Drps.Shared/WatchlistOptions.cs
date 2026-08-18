namespace Drps.Shared;

// Bound from configuration section "Watchlist" via IOptions<WatchlistOptions>. Single
// source of truth for the symbol universe Drps.Ingestion and Drps.Calculator both operate
// on - previously each project carried its own independent copy of this array in its own
// appsettings.json, and the two drifted out of sync once already. Both projects now load
// the same shared-watchlist.appsettings.json (repo root, checked into source control since
// this isn't a secret) via AddJsonFile, so there is exactly one array to edit.
public class WatchlistOptions
{
    public string[] Symbols { get; set; } = Array.Empty<string>();
}
