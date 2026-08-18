using System.Diagnostics;
using System.Text.Json;

namespace Drps.Diagnostics;

// Deliberately NOT reusing AlpacaFeeder or FeederRetryPolicy from Drps.Ingestion - this tool
// exists specifically to observe RAW behavior against the live API (real error codes, real
// truncation, real rate-limit headers). Wrapping calls in the production retry policy would
// mask exactly the signal this probe exists to capture - e.g. a 429 would be silently retried
// away instead of being recorded as a real rate-limit hit.
public sealed class AlpacaBandwidthProbe
{
    private readonly HttpClient _httpClient;
    private readonly DiagnosticLog _log;

    public AlpacaBandwidthProbe(HttpClient httpClient, DiagnosticLog log)
    {
        _httpClient = httpClient;
        _log = log;
    }

    public async Task<BatchProbeResult> ProbeBatchAsync(
        IReadOnlyList<string> symbols, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        var symbolParam = string.Join(',', symbols);

        // limit=10000 matches Alpaca's own documented max page size for this endpoint - set
        // explicitly rather than left to default, so a real pagination requirement (Stage 1's
        // whole reason for using a wide date window) can only come from genuinely exceeding
        // that ceiling, not from an unrelated smaller default.
        var url = $"/v2/stocks/bars?symbols={Uri.EscapeDataString(symbolParam)}" +
                  $"&timeframe=1Day&adjustment=raw&start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}&feed=iex&limit=10000";

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            stopwatch.Stop();

            var rateLimitHeaders = ExtractRateLimitHeaders(response);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new BatchProbeResult
                {
                    RequestedSymbolCount = symbols.Count,
                    Success = false,
                    HttpStatusCode = (int)response.StatusCode,
                    ErrorMessage = Truncate(body, 500),
                    Duration = stopwatch.Elapsed,
                    RateLimitHeaders = rateLimitHeaders
                };
            }

            using var doc = JsonDocument.Parse(body);
            var symbolsReturned = 0;
            if (doc.RootElement.TryGetProperty("bars", out var barsElement) && barsElement.ValueKind == JsonValueKind.Object)
                symbolsReturned = barsElement.EnumerateObject().Count();

            var nextPageTokenPresent = doc.RootElement.TryGetProperty("next_page_token", out var tokenElement)
                && tokenElement.ValueKind == JsonValueKind.String;

            return new BatchProbeResult
            {
                RequestedSymbolCount = symbols.Count,
                Success = true,
                HttpStatusCode = (int)response.StatusCode,
                SymbolsWithBarsReturned = symbolsReturned,
                NextPageTokenPresent = nextPageTokenPresent,
                Duration = stopwatch.Elapsed,
                RateLimitHeaders = rateLimitHeaders
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _log.Warn($"  [batch={symbols.Count}] request threw: {ex.GetType().Name}: {ex.Message}");
            return new BatchProbeResult
            {
                RequestedSymbolCount = symbols.Count,
                Success = false,
                ErrorMessage = $"{ex.GetType().Name}: {ex.Message}",
                Duration = stopwatch.Elapsed
            };
        }
    }

    // Alpaca's actual rate-limit header names are not consistently documented across sources -
    // the exact conflict this whole probe exists to resolve empirically - so this captures
    // anything header-shaped containing "rate limit" rather than guessing one exact name up
    // front, and lets the real response decide what gets logged.
    private static Dictionary<string, string> ExtractRateLimitHeaders(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            if (header.Key.Contains("ratelimit", StringComparison.OrdinalIgnoreCase)
                || header.Key.Contains("rate-limit", StringComparison.OrdinalIgnoreCase))
            {
                result[header.Key] = string.Join(",", header.Value);
            }
        }

        return result;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
