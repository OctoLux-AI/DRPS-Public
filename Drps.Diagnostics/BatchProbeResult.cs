namespace Drps.Diagnostics;

public sealed class BatchProbeResult
{
    public required int RequestedSymbolCount { get; init; }
    public bool Success { get; init; }
    public int? HttpStatusCode { get; init; }
    public string? ErrorMessage { get; init; }
    public int SymbolsWithBarsReturned { get; init; }
    public bool NextPageTokenPresent { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyDictionary<string, string> RateLimitHeaders { get; init; } = new Dictionary<string, string>();
}
