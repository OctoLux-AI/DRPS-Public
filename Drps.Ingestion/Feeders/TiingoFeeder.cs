using System.Net;
using System.Text.Json;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Microsoft.Extensions.Options;
using Polly.Retry;

namespace Drps.Ingestion.Feeders;

public class TiingoFeeder : IMarketDataFeeder
{
    public const string HttpClientName = "TiingoClient";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TiingoOptions _options;
    private readonly ILogger<TiingoFeeder> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public TiingoFeeder(IHttpClientFactory httpClientFactory, IOptions<TiingoOptions> options, ILogger<TiingoFeeder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _retryPolicy = FeederRetryPolicy.Build("TIINGO", logger);
    }

    public SourceType Source => SourceType.Tiingo;

    public async Task<FeedFetchResult> FetchDailyBarsAsync(string symbol, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        // Checked before any HTTP call is attempted (outside the try/catch below) so a
        // missing credential throws ConfigurationMissingException and propagates to the
        // caller distinctly, rather than being converted into a generic Success = false
        // result indistinguishable from a real source-side rejection.
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ConfigurationMissingException("Tiingo:ApiKey");

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var url = $"/tiingo/daily/{Uri.EscapeDataString(symbol)}/prices" +
                      $"?startDate={start:yyyy-MM-dd}&endDate={end:yyyy-MM-dd}" +
                      $"&token={Uri.EscapeDataString(_options.ApiKey)}";

            var response = await _retryPolicy.ExecuteAsync(
                token => client.GetAsync(url, token), cancellationToken);

            // A 404 here means Tiingo explicitly confirmed no data exists for this symbol
            // (unknown/delisted ticker) — not a real fetch failure, so it must not be folded
            // into the generic Success = false path below. Checked before
            // EnsureSuccessStatusCode() and rethrown past the catch-all further down so it
            // propagates to IngestionRunner distinctly, same carve-out precedent as
            // ConfigurationMissingException.
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new SymbolNotFoundException(symbol);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(json);

            // Tiingo's daily prices endpoint returns a bare top-level JSON array. Anything
            // else is an unexpected response shape (contract drift), not a legitimate result.
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new FeedFetchResult
                {
                    Success = false,
                    Bars = Array.Empty<RawOhlcvBar>(),
                    ErrorMessage = "Tiingo response was not a JSON array (unexpected response shape)"
                };
            }

            var bars = MapBars(symbol, doc.RootElement);

            // Ex-dividend extraction rides the same already-parsed response — no second HTTP
            // call — but is deliberately isolated in its own try/catch, separate from the
            // OHLCV mapping above. CLAUDE.md's "Ex-Dividend Source: Tiingo Replaces Finnhub"
            // decision (2026-08-01) treats dividend data as a lower-stakes derived add-on;
            // OHLCV bars are what DMA/RSI/RVOL/ATR/Gate all depend on downstream, so a
            // divCash-parsing problem (e.g. unexpected contract drift) must never fail the
            // whole fetch and take real bar data down with it.
            var exDividendObservations = Array.Empty<RawExDividendObservation>() as IReadOnlyList<RawExDividendObservation>;
            try
            {
                exDividendObservations = TiingoExDividendMapper.MapObservations(symbol, doc.RootElement);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[TIINGO-FEEDER]: ex-dividend extraction failed for {Symbol} - OHLCV bars still returned normally",
                    symbol);
            }

            // Mirrors AlpacaFeeder's NoDataForRange treatment: a real, listed symbol whose
            // requested range predates its IPO, falls entirely on non-trading days, or is
            // delisted with no data in that window all return this same 200+"[]" shape as
            // an unknown ticker would (short of the 404 case above, already carved out via
            // SymbolNotFoundException). Ambiguous either way, so flagged rather than given
            // an unqualified success credit.
            return new FeedFetchResult
            {
                Success = true,
                Bars = bars,
                NoDataForRange = bars.Count == 0,
                ExDividendObservations = exDividendObservations
            };
        }
        catch (SymbolNotFoundException)
        {
            // Must propagate to the caller uncaught — the catch-all below would otherwise
            // convert it into an indistinguishable Success = false result.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TIINGO-FEEDER]: fetch failed for {Symbol}", symbol);
            return new FeedFetchResult { Success = false, Bars = Array.Empty<RawOhlcvBar>(), ErrorMessage = ex.Message };
        }
    }

    private static IReadOnlyList<RawOhlcvBar> MapBars(string symbol, JsonElement valuesElement)
    {
        var bars = new List<RawOhlcvBar>();
        var ingestedAt = DateTimeOffset.UtcNow;

        foreach (var entry in valuesElement.EnumerateArray())
        {
            // Tiingo's "date" is an ISO 8601 timestamp (e.g. "2024-06-10T00:00:00.000Z").
            // Truncated to the UTC date component, same as AlpacaFeeder, so daily bars from
            // both sources join correctly on (Symbol, Timestamp, Resolution).
            var barDate = DateOnly.FromDateTime(entry.GetProperty("date").GetDateTimeOffset().UtcDateTime);

            // Deliberately reading only the raw open/high/low/close/volume fields — never
            // adjOpen/adjHigh/adjLow/adjClose/adjVolume. Tiingo retroactively split-adjusts
            // those adjusted fields even for historical dates, which is incompatible with
            // DRPS's raw-bar verification requirement (see CLAUDE.md, confirmed empirically
            // against NVDA's 2024-06-10 10-for-1 split).
            bars.Add(new RawOhlcvBar
            {
                Source = SourceType.Tiingo,
                Symbol = symbol,
                Timestamp = new DateTimeOffset(barDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                Resolution = "1Day",
                Open = entry.GetProperty("open").GetDecimal(),
                High = entry.GetProperty("high").GetDecimal(),
                Low = entry.GetProperty("low").GetDecimal(),
                Close = entry.GetProperty("close").GetDecimal(),
                Volume = entry.GetProperty("volume").GetInt64(),
                AdjustmentType = "raw",
                IngestedAt = ingestedAt,
                RequestId = Guid.NewGuid()
            });
        }

        return bars;
    }
}
