using System.Net;
using Drps.Shared.Models;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace Drps.Ingestion.Finnhub;

// ADJUSTMENT WARNING — unconfirmed, do not treat as raw without re-verifying:
// CLAUDE.md requires all OHLCV sources to be requested/configured for raw, unadjusted
// prices. This client passes adjusted=false explicitly (never relies on Finnhub's
// default), but that parameter's actual effect on /stock/candle is disputed:
//   - A long-standing open Finnhub-API GitHub issue (#336, "Requests for unadjusted
//     candles now return adjusted data") reports adjusted=false and adjusted=true
//     returning identical, split-adjusted output.
//   - Independent empirical spot-checking (Robot Wealth's Finnhub API write-up)
//     found candle data adjusted for both splits and dividends even without
//     requesting adjustment.
//   - Finnhub's own hosted docs page is a JS-rendered SPA that couldn't be fetched
//     for a first-party confirmation either way.
// Net result: whether this endpoint can actually deliver raw prices is NOT confirmed.
// Until verified against a live key (ideally a ticker with a known recent split), do
// not assume Finnhub bars are tolerance-comparable to Alpaca's raw bars — a wide
// divergence on a split-affected ticker may be a convention mismatch, not a data
// quality problem.
//
// Separately, multiple independent sources report Finnhub moved historical
// /stock/candle access behind a paid plan, returning 403 on free-tier keys — also
// unconfirmed against a real key here, but worth checking before assuming this
// integration is free-tier-viable at all.
public class FinnhubClient
{
    private const int MaxRetries = 3;

    private readonly HttpClient _httpClient;
    private readonly FinnhubOptions _options;
    private readonly ILogger<FinnhubClient> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public FinnhubClient(HttpClient httpClient, IOptions<FinnhubOptions> options, ILogger<FinnhubClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _retryPolicy = BuildRetryPolicy(logger);
    }

    // Same retry boundary as AlpacaFeeder: all retries happen inside one fetch attempt,
    // exhausting MaxRetries before control returns to the caller. A "strike" for the
    // future dead-source counter is one exhausted FetchDailyBarsAsync call, not one retry.
    private static AsyncRetryPolicy<HttpResponseMessage> BuildRetryPolicy(ILogger logger)
    {
        return Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .OrResult(response => IsTransient(response.StatusCode))
            .WaitAndRetryAsync(
                retryCount: MaxRetries,
                sleepDurationProvider: (attempt, outcome, _) =>
                    outcome.Result?.Headers.RetryAfter?.Delta
                        ?? TimeSpan.FromSeconds(Math.Pow(2, attempt)),
                onRetryAsync: (outcome, delay, attempt, _) =>
                {
                    logger.LogWarning(
                        "[FINNHUB/RETRY]: attempt {Attempt}/{MaxRetries} failed ({Reason}), retrying in {Delay}s",
                        attempt, MaxRetries,
                        outcome.Exception?.Message ?? outcome.Result?.StatusCode.ToString(),
                        delay.TotalSeconds);
                    return Task.CompletedTask;
                });
    }

    // 429 (rate limit) and 408 (timeout) are transient by nature; 5xx is the server's
    // own failure. Anything else (401/403/404/etc.) is a genuine error retrying won't
    // fix, so it is not retried.
    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests
        || statusCode == HttpStatusCode.RequestTimeout
        || (int)statusCode >= 500;

    public async Task<IReadOnlyList<OhlcvBar>> GetDailyBarsAsync(string ticker, int tradingDays, CancellationToken ct = default)
    {
        // Request a wider calendar window than tradingDays to absorb weekends/holidays;
        // the mapper returns whatever Finnhub actually reports in range.
        var end = DateTimeOffset.UtcNow;
        var start = end.AddDays(-tradingDays * 3);

        var url = $"{_options.BaseUrl}/stock/candle?symbol={Uri.EscapeDataString(ticker)}&resolution=D" +
                  $"&from={start.ToUnixTimeSeconds()}&to={end.ToUnixTimeSeconds()}&adjusted=false";

        var response = await _retryPolicy.ExecuteAsync(async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-Finnhub-Token", _options.ApiKey);
            return await _httpClient.SendAsync(request, token);
        }, ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var bars = FinnhubBarMapper.MapBars(ticker, json, DateTime.UtcNow);

        foreach (var bar in bars)
        {
            _logger.LogInformation(
                "[FINNHUB/BARS]: {Ticker} {BarDate} O={Open} H={High} L={Low} C={Close} V={Volume} Source={Source} Verified={Verified}",
                bar.Ticker, bar.BarDate, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, bar.Source, bar.Verified);
        }

        return bars;
    }
}
