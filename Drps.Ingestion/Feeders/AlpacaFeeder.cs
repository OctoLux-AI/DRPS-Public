using System.Text.Json;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Microsoft.Extensions.Options;
using Polly.Retry;

namespace Drps.Ingestion.Feeders;

public class AlpacaFeeder : IMarketDataFeeder
{
    public const string HttpClientName = "AlpacaClient";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AlpacaOptions _options;
    private readonly ILogger<AlpacaFeeder> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public AlpacaFeeder(IHttpClientFactory httpClientFactory, IOptions<AlpacaOptions> options, ILogger<AlpacaFeeder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _retryPolicy = FeederRetryPolicy.Build("ALPACA", logger);
    }

    public SourceType Source => SourceType.Alpaca;

    public async Task<FeedFetchResult> FetchDailyBarsAsync(string symbol, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        // Checked before any HTTP call is attempted (outside the try/catch below) so a
        // missing credential throws ConfigurationMissingException and propagates to the
        // caller distinctly, rather than being converted into a generic Success = false
        // result indistinguishable from a real source-side rejection.
        if (string.IsNullOrWhiteSpace(_options.KeyId))
            throw new ConfigurationMissingException("Alpaca:KeyId");
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ConfigurationMissingException("Alpaca:SecretKey");

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var url = $"/v2/stocks/{Uri.EscapeDataString(symbol)}/bars" +
                      $"?timeframe=1Day&adjustment=raw&start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}&feed=iex";

            var response = await _retryPolicy.ExecuteAsync(
                token => client.GetAsync(url, token), cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var bars = MapBars(symbol, json);

            // Confirmed empirically (real request against an invalid ticker): Alpaca
            // returns 200 with a zero-length "bars" array for an unknown/delisted symbol,
            // not a 404 - the same shape it would return for a legitimate but genuinely
            // empty range (e.g. a market holiday, or a gap before a ticker's IPO date).
            // Flagged rather than treated as an unqualified success so orchestration
            // doesn't let this silently count toward trust-building.
            return new FeedFetchResult { Success = true, Bars = bars, NoDataForRange = bars.Count == 0 };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ALPACA-FEEDER]: fetch failed for {Symbol}", symbol);
            return new FeedFetchResult { Success = false, Bars = Array.Empty<RawOhlcvBar>(), ErrorMessage = ex.Message };
        }
    }

    private static IReadOnlyList<RawOhlcvBar> MapBars(string symbol, string responseJson)
    {
        var bars = new List<RawOhlcvBar>();

        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("bars", out var barsElement) || barsElement.ValueKind != JsonValueKind.Array)
            return bars;

        var ingestedAt = DateTimeOffset.UtcNow;

        foreach (var bar in barsElement.EnumerateArray())
        {
            // Alpaca's "t" is midnight ET expressed in UTC (e.g. 04:00:00Z), not midnight
            // UTC. Twelve Data normalizes its bare date strings to exactly midnight UTC, so
            // without truncating here the same trading day from the two sources never
            // matches on (Symbol, Timestamp, Resolution) during BarVerification reconciliation
            // — confirmed via empirical testing against both APIs on 2026-07-10. Daily-only:
            // do not apply this truncation if this feeder ever produces intraday bars.
            var barDate = DateOnly.FromDateTime(bar.GetProperty("t").GetDateTimeOffset().UtcDateTime);

            bars.Add(new RawOhlcvBar
            {
                Source = SourceType.Alpaca,
                Symbol = symbol,
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

        return bars;
    }
}
