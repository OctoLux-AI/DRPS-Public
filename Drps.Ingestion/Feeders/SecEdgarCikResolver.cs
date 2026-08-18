using System.Text.Json;
using Microsoft.Extensions.Options;
using Polly.Retry;

namespace Drps.Ingestion.Feeders;

/// <summary>
/// Thread-safe, time-boxed cache for SEC's company_tickers.json ticker-to-CIK mapping. SEC
/// updates this ~8MB file at most once daily per its own guidance, so refetching it per
/// ticker would be wasteful and draws down goodwill under SEC's fair-use policy for no
/// benefit - cached for 24 hours instead. Registered as a singleton (not scoped) so the
/// cached mapping is shared across every DI scope in the process: SectorWorker creates a
/// fresh scope per ticker in its watchlist loop, so a scoped cache here would be rebuilt
/// (and refetched) on every single ticker, defeating the point of caching at all.
/// </summary>
public class SecEdgarCikResolver
{
    public const string HttpClientName = "SecEdgarCikClient";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SecEdgarOptions _options;
    private readonly ILogger<SecEdgarCikResolver> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyDictionary<string, string> _tickerToCik = new Dictionary<string, string>();
    private DateTime _cachedAt = DateTime.MinValue;

    public SecEdgarCikResolver(IHttpClientFactory httpClientFactory, IOptions<SecEdgarOptions> options, ILogger<SecEdgarCikResolver> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _retryPolicy = FeederRetryPolicy.Build("SEC-EDGAR-CIK", logger);
    }

    // Returns null if the ticker has no known CIK mapping, or the underlying mapping fetch
    // itself failed - both are legitimate "cannot resolve" outcomes for the caller, never
    // thrown, matching this codebase's fail-closed feeder conventions.
    public async Task<string?> ResolveCikAsync(string ticker, CancellationToken cancellationToken)
    {
        await EnsureFreshAsync(cancellationToken);
        return _tickerToCik.TryGetValue(ticker.ToUpperInvariant(), out var cik) ? cik : null;
    }

    private async Task EnsureFreshAsync(CancellationToken cancellationToken)
    {
        if (IsFresh())
            return;

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check after acquiring the lock - another caller may have already
            // refreshed the mapping while this one was waiting.
            if (IsFresh())
                return;

            var client = _httpClientFactory.CreateClient(HttpClientName);

            var response = await _retryPolicy.ExecuteAsync(async token =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, _options.CompanyTickersUrl);
                request.Headers.UserAgent.ParseAdd(_options.UserAgent);
                return await client.SendAsync(request, token);
            }, cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            _tickerToCik = ParseTickerMap(json);
            _cachedAt = DateTime.UtcNow;

            _logger.LogInformation(
                "[SEC-EDGAR-CIK]: refreshed ticker-to-CIK mapping, {Count} entries", _tickerToCik.Count);
        }
        catch (Exception ex)
        {
            // A failed refresh leaves whatever mapping was already cached in place (empty
            // on a first-ever failure) rather than throwing - a caller resolving a ticker
            // just gets a "not found" result, same fail-closed shape as every other feeder.
            _logger.LogError(ex, "[SEC-EDGAR-CIK]: failed to refresh ticker-to-CIK mapping");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool IsFresh() => DateTime.UtcNow - _cachedAt < CacheTtl && _tickerToCik.Count > 0;

    // Public, not private - exercised directly by unit tests against a fake payload
    // without needing to fake an HttpClient round-trip just to test the parsing, same
    // precedent as FinnhubBarMapper/NextRunTimeCalculator's public static helpers.
    public static IReadOnlyDictionary<string, string> ParseTickerMap(string json)
    {
        var result = new Dictionary<string, string>();
        using var doc = JsonDocument.Parse(json);

        // SEC's company_tickers.json is a JSON object keyed by an arbitrary numeric index
        // string ("0", "1", "2", ...), each value shaped {"cik_str": int, "ticker": string,
        // "title": string} - not a top-level array, despite being logically list-shaped.
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var entry = property.Value;
            if (!entry.TryGetProperty("ticker", out var tickerElement)
                || !entry.TryGetProperty("cik_str", out var cikElement))
            {
                continue;
            }

            var ticker = tickerElement.GetString();
            if (string.IsNullOrWhiteSpace(ticker))
                continue;

            // SEC's submissions endpoint expects a zero-padded 10-digit CIK in the URL path
            // (CIK{10 digits}.json) - padded once here so every caller downstream gets a
            // ready-to-use value instead of re-deriving this format itself.
            var cik = cikElement.GetInt64().ToString("D10");
            result[ticker.ToUpperInvariant()] = cik;
        }

        return result;
    }
}
