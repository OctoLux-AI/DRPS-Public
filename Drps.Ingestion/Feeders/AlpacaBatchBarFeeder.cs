using System.Text.Json;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Microsoft.Extensions.Options;
using Polly.Retry;

namespace Drps.Ingestion.Feeders;

/// <summary>
/// Batched multi-symbol counterpart to AlpacaFeeder's single-symbol FetchDailyBarsAsync - used
/// exclusively by the nightly full-universe bar sweep (UniverseBarSweepRunner), never by the
/// narrower watchlist-scoped per-symbol pipeline (Worker/IngestionRunner). Reuses
/// AlpacaFeeder's own named HttpClient (same base address, same baked-in auth headers) rather
/// than registering a second one - same underlying account and credentials, just a different
/// endpoint shape.
///
/// Batch size is the caller's responsibility (UniverseBarSweepRunner.ConfirmedMaxBatchSize,
/// 500 - the real, empirically-confirmed ceiling recorded in CLAUDE.md's "Alpaca Bandwidth —
/// Empirical Findings (locked)" block); this class does not itself enforce or guess at a
/// limit, it just fetches whatever symbol list it's given in one call.
///
/// Pagination (next_page_token) is NOT implemented - a known, documented v1 gap, not an
/// oversight. The nightly sweep's actual shape (500 symbols x ~1 day's worth of bars) sits
/// comfortably under Alpaca's ~10,000-data-point response cap per the same locked findings, so
/// this should never fire in practice; if it ever does (e.g. a future wider lookback), only
/// the first page's bars are returned and BatchFeedFetchResult.NextPageTokenPresent flags the
/// gap so the caller can log it rather than silently miss data.
/// </summary>
public class AlpacaBatchBarFeeder : IAlpacaBatchBarFeeder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AlpacaOptions _options;
    private readonly ILogger<AlpacaBatchBarFeeder> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public AlpacaBatchBarFeeder(IHttpClientFactory httpClientFactory, IOptions<AlpacaOptions> options, ILogger<AlpacaBatchBarFeeder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _retryPolicy = FeederRetryPolicy.Build("ALPACA-BATCH", logger);
    }

    public async Task<BatchFeedFetchResult> FetchBatchAsync(
        IReadOnlyList<string> symbols, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        // Same pre-flight credential check as every other feeder, checked before any HTTP
        // call is attempted - a missing credential throws ConfigurationMissingException and
        // propagates distinctly, rather than being converted into a generic Success = false
        // result indistinguishable from a real source-side rejection.
        if (string.IsNullOrWhiteSpace(_options.KeyId))
            throw new ConfigurationMissingException("Alpaca:KeyId");
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ConfigurationMissingException("Alpaca:SecretKey");

        try
        {
            var client = _httpClientFactory.CreateClient(AlpacaFeeder.HttpClientName);
            var symbolParam = string.Join(',', symbols);

            // limit=10000 matches Alpaca's own documented max page size for this endpoint,
            // same as the empirical probe that confirmed the 500-symbol batch ceiling.
            var url = $"/v2/stocks/bars?symbols={Uri.EscapeDataString(symbolParam)}" +
                      $"&timeframe=1Day&adjustment=raw&start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}&feed=iex&limit=10000";

            var response = await _retryPolicy.ExecuteAsync(
                token => client.GetAsync(url, token), cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var (barsBySymbol, nextPageTokenPresent) = MapBars(json);

            return new BatchFeedFetchResult
            {
                Success = true,
                BarsBySymbol = barsBySymbol,
                NextPageTokenPresent = nextPageTokenPresent
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ALPACA-BATCH-FEEDER]: batch fetch failed for {Count} symbols", symbols.Count);
            return new BatchFeedFetchResult { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static (Dictionary<string, IReadOnlyList<RawOhlcvBar>> BarsBySymbol, bool NextPageTokenPresent) MapBars(string responseJson)
    {
        var result = new Dictionary<string, IReadOnlyList<RawOhlcvBar>>();
        using var doc = JsonDocument.Parse(responseJson);

        // Multi-symbol shape: {"bars": {"AAPL": [...], "MSFT": [...]}, "next_page_token": ...} -
        // an object keyed by symbol, not the single-symbol endpoint's flat array under "bars".
        if (doc.RootElement.TryGetProperty("bars", out var barsElement) && barsElement.ValueKind == JsonValueKind.Object)
        {
            var ingestedAt = DateTimeOffset.UtcNow;

            foreach (var symbolProperty in barsElement.EnumerateObject())
            {
                if (symbolProperty.Value.ValueKind != JsonValueKind.Array)
                    continue;

                var bars = new List<RawOhlcvBar>();
                foreach (var bar in symbolProperty.Value.EnumerateArray())
                {
                    // Same midnight-ET-as-UTC truncation AlpacaFeeder's own MapBars already
                    // applies - required for these rows to join correctly on (Symbol,
                    // Timestamp, Resolution) with any other source, should this bar ever be
                    // picked up by the separate, narrower dual-source verification path later.
                    var barDate = DateOnly.FromDateTime(bar.GetProperty("t").GetDateTimeOffset().UtcDateTime);

                    bars.Add(new RawOhlcvBar
                    {
                        Source = SourceType.Alpaca,
                        Symbol = symbolProperty.Name,
                        Timestamp = new DateTimeOffset(barDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                        Resolution = "1Day",
                        Open = bar.GetProperty("o").GetDecimal(),
                        High = bar.GetProperty("h").GetDecimal(),
                        Low = bar.GetProperty("l").GetDecimal(),
                        Close = bar.GetProperty("c").GetDecimal(),
                        Volume = bar.GetProperty("v").GetInt64(),
                        AdjustmentType = "raw",
                        IngestedAt = ingestedAt,
                        RequestId = Guid.NewGuid()
                    });
                }

                // A symbol present in the response with a zero-length array is the same
                // "no bars for this symbol" outcome as a symbol absent entirely - normalized
                // to absence here so the caller has exactly one shape to check, not two.
                if (bars.Count > 0)
                    result[symbolProperty.Name] = bars;
            }
        }

        var nextPageTokenPresent = doc.RootElement.TryGetProperty("next_page_token", out var tokenElement)
            && tokenElement.ValueKind == JsonValueKind.String;

        return (result, nextPageTokenPresent);
    }
}
