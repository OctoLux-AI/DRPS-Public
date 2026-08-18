using System.Text.Json;
using Drps.Shared.Models;
using Microsoft.Extensions.Options;
using Polly.Retry;

namespace Drps.Ingestion.Feeders;

// Genuinely informational only, per the decision to add this source: SecEdgar rows are
// never compared against Finnhub's - the two sources use incompatible classification
// taxonomies (Finnhub's own industry scheme vs. decades-old government SIC codes) - so
// this feeder implements no comparison/reconciliation logic of any kind, only mapping and
// persistence. Verified is always false (see RawSectorObservation's own doc comment).
public class SecEdgarSectorFeeder : ISectorFeeder
{
    public const string HttpClientName = "SecEdgarSubmissionsClient";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SecEdgarCikResolver _cikResolver;
    private readonly SecEdgarRateLimiter _rateLimiter;
    private readonly SecEdgarOptions _options;
    private readonly ILogger<SecEdgarSectorFeeder> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public SecEdgarSectorFeeder(
        IHttpClientFactory httpClientFactory,
        SecEdgarCikResolver cikResolver,
        SecEdgarRateLimiter rateLimiter,
        IOptions<SecEdgarOptions> options,
        ILogger<SecEdgarSectorFeeder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cikResolver = cikResolver;
        _rateLimiter = rateLimiter;
        _options = options.Value;
        _logger = logger;
        _retryPolicy = FeederRetryPolicy.Build("SEC-EDGAR-SECTOR", logger);
    }

    public SectorSourceType Source => SectorSourceType.SecEdgar;

    public async Task<SectorFetchResult> FetchSectorAsync(string ticker, CancellationToken cancellationToken)
    {
        try
        {
            var cik = await _cikResolver.ResolveCikAsync(ticker, cancellationToken);
            if (cik is null)
            {
                // No known CIK mapping for this ticker (unrecognized ticker, or the
                // ticker-map fetch itself failed) - a legitimate "cannot resolve" outcome,
                // not a source-side rejection. No HTTP call to the submissions endpoint is
                // even attempted. Same ambiguous-no-data shape every other feeder in this
                // codebase already uses (ExDividend/Finnhub sector's NoDataForRange).
                _logger.LogWarning("[SEC-EDGAR-SECTOR-FEEDER]: no CIK mapping found for {Ticker} - skipped", ticker);
                return new SectorFetchResult { Success = true, Observations = Array.Empty<RawSectorObservation>(), NoDataForRange = true };
            }

            // Proactive self-throttling ahead of the request, not reactive to a 429 (SEC
            // publishes no per-key rate limit at all) - see SecEdgarRateLimiter's own doc
            // comment.
            await _rateLimiter.WaitForSlotAsync(cancellationToken);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            var url = $"{_options.SubmissionsBaseUrl}/CIK{cik}.json";

            var response = await _retryPolicy.ExecuteAsync(async token =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd(_options.UserAgent);
                return await client.SendAsync(request, token);
            }, cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            // SEC's submissions endpoint returns a bare top-level JSON object. Anything
            // else is an unexpected response shape (contract drift), not a legitimate
            // result.
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new SectorFetchResult
                {
                    Success = false,
                    Observations = Array.Empty<RawSectorObservation>(),
                    ErrorMessage = "SEC EDGAR response was not a JSON object (unexpected response shape)"
                };
            }

            // A resolvable CIK with no "sic" value (some entity types, e.g. certain funds,
            // carry no SIC classification) is the same ambiguous-emptiness shape as an
            // unrecognized ticker - deliberately not distinguished further.
            if (!doc.RootElement.TryGetProperty("sic", out var sicElement)
                || sicElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(sicElement.GetString()))
            {
                return new SectorFetchResult { Success = true, Observations = Array.Empty<RawSectorObservation>(), NoDataForRange = true };
            }

            var sicDescription = doc.RootElement.TryGetProperty("sicDescription", out var descriptionElement)
                && descriptionElement.ValueKind == JsonValueKind.String
                    ? descriptionElement.GetString()
                    : null;

            var observation = new RawSectorObservation
            {
                Ticker = ticker,
                Source = SectorSourceType.SecEdgar,
                SectorValue = sicDescription,
                SicCode = sicElement.GetString(),
                FetchedAt = DateTime.UtcNow,
                Verified = false
            };

            return new SectorFetchResult { Success = true, Observations = new[] { observation } };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SEC-EDGAR-SECTOR-FEEDER]: fetch failed for {Ticker}", ticker);
            return new SectorFetchResult { Success = false, Observations = Array.Empty<RawSectorObservation>(), ErrorMessage = ex.Message };
        }
    }
}
