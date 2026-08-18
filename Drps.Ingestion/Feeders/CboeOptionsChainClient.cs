using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Drps.Ingestion.Feeders;

/// <summary>
/// Live, single-call put/call volume-ratio fetch against Cboe's delayed-quotes options
/// endpoint (CBOE delayed-quotes options endpoint audit, confirmed live and no-auth
/// 2026-07-29). GET https://cdn.cboe.com/api/global/delayed_quotes/options/{TICKER}.json - no
/// underscore prefix for equities (that prefix is index-only, e.g. _SPX, per the same audit).
///
/// The response has no aggregate put/call ratio - or even a total-put-volume/total-call-volume
/// - field anywhere; only a flat data.options[] array of per-contract rows. This client sums
/// `volume` across every contract, split by the option-type character at the fixed
/// strike-type position in the OCC-style `option` symbol (e.g. "AAPL260729C00205000"). The
/// last 9 characters of any OCC symbol are always {C|P} + an 8-digit strike, regardless of
/// ticker length, so the type character is always symbol[^9] - never a naive Contains('C')/
/// Contains('P') check, which would misfire on a ticker whose own letters happen to include C
/// or P (e.g. COST, MRVL). data.volume (the top-level field) is the underlying equity's own
/// share volume, not an options aggregate, and is deliberately never read here.
///
/// No caching, no TTL - matches the confirmed Cache-Control: max-age=0, s-maxage=5 (a
/// ~5-second live feed), unlike this project's slow-moving-reference-data TTL convention
/// (sector/earnings/insider, all 7-day-class). Every call fetches fresh.
///
/// Deliberately no persistence and no Polly retry policy, same precedent as
/// MarketauxSentimentClient - a fire-time read for an Adjuster-lane sizing signal, not a
/// source DRPS accumulates history from. Any HTTP failure, malformed JSON, unexpected
/// response shape, or a non-positive total-call-volume denominator (would otherwise divide by
/// zero, or produce a nonsensical ratio) all fail closed to null - "no usable signal", never
/// an exception propagated to the caller. Every failure mode is still logged distinctly
/// internally for diagnosis, even though the public contract only ever exposes null vs. a
/// real ratio.
///
/// Not wired into DI/Program.cs, AdjusterSizingService, MultiSignalMultiplierCombiner, or any
/// other caller yet - deliberately standalone, per this task's own explicit scope. That wiring
/// (an OptionsFlowMultiplierService-consumed source for the ratio OptionsFlowMultiplierService
/// already knows how to score) is a separate, later task.
/// </summary>
public class CboeOptionsChainClient
{
    public const string HttpClientName = "CboeOptionsChainClient";
    public const string BaseUrl = "https://cdn.cboe.com/api/global/delayed_quotes/options";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CboeOptionsChainClient> _logger;

    public CboeOptionsChainClient(IHttpClientFactory httpClientFactory, ILogger<CboeOptionsChainClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<decimal?> GetPutCallRatioAsync(string ticker, CancellationToken cancellationToken)
    {
        var upperTicker = ticker.ToUpperInvariant();
        var url = $"{BaseUrl}/{Uri.EscapeDataString(upperTicker)}.json";

        HttpResponseMessage response;
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            response = await client.GetAsync(url, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CBOE-OPTIONS-CHAIN-CLIENT]: fetch failed for {Ticker}", upperTicker);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "[CBOE-OPTIONS-CHAIN-CLIENT]: non-success status {StatusCode} for {Ticker}",
                (int)response.StatusCode, upperTicker);
            return null;
        }

        try
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ComputePutCallRatio(json, upperTicker, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CBOE-OPTIONS-CHAIN-CLIENT]: parse failed for {Ticker}", upperTicker);
            return null;
        }
    }

    // Public, not private - testable directly against a raw response body without needing an
    // HTTP round-trip, same "public static helper" precedent as
    // MarketauxSentimentClient.ParseResponse. Unlike that method, this one never throws for a
    // genuinely-empty-but-well-formed options[] array (a real "nothing traded" result) - the
    // zero-call-volume guard below returns null for that case the same way it does for a
    // real chain with zero call volume. It only throws (caught by GetPutCallRatioAsync's own
    // try/catch above, fail-closed to null the same way) for a genuinely malformed body or an
    // unexpected top-level shape, matching this project's existing "unexpected shape is a
    // fetch-level failure" precedent.
    public static decimal? ComputePutCallRatio(string json, string ticker, ILogger? logger = null)
    {
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("data", out var dataElement)
            || dataElement.ValueKind != JsonValueKind.Object
            || !dataElement.TryGetProperty("options", out var optionsElement)
            || optionsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Cboe delayed-quotes options response was not in the expected data.options[] shape");
        }

        var totalCallVolume = 0m;
        var totalPutVolume = 0m;

        foreach (var contract in optionsElement.EnumerateArray())
        {
            if (contract.ValueKind != JsonValueKind.Object
                || !contract.TryGetProperty("option", out var optionElement)
                || optionElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var symbol = optionElement.GetString();
            if (string.IsNullOrEmpty(symbol) || symbol.Length < 9)
            {
                logger?.LogWarning(
                    "[CBOE-OPTIONS-CHAIN-CLIENT]: skipping unparseable option symbol for {Ticker}: {Symbol}", ticker, symbol);
                continue;
            }

            if (!contract.TryGetProperty("volume", out var volumeElement) || volumeElement.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            var volume = volumeElement.GetDecimal();

            // OCC-style symbol's last 9 characters are always {C|P} + an 8-digit strike,
            // regardless of ticker length - the type character sits at symbol[^9], not
            // wherever a naive Contains('C')/Contains('P') might match inside the ticker
            // itself (e.g. COST, MRVL).
            var typeChar = symbol[^9];
            switch (typeChar)
            {
                case 'C':
                    totalCallVolume += volume;
                    break;
                case 'P':
                    totalPutVolume += volume;
                    break;
                default:
                    logger?.LogWarning(
                        "[CBOE-OPTIONS-CHAIN-CLIENT]: unrecognized option type character '{TypeChar}' for {Ticker}: {Symbol}",
                        typeChar, ticker, symbol);
                    break;
            }
        }

        if (totalCallVolume <= 0m)
        {
            return null;
        }

        return totalPutVolume / totalCallVolume;
    }
}
