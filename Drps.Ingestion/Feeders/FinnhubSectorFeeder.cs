using System.Text.Json;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Microsoft.Extensions.Options;
using Polly.Retry;

namespace Drps.Ingestion.Feeders;

public class FinnhubSectorFeeder : ISectorFeeder
{
    public const string HttpClientName = "FinnhubSectorClient";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FinnhubOptions _options;
    private readonly ILogger<FinnhubSectorFeeder> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public FinnhubSectorFeeder(IHttpClientFactory httpClientFactory, IOptions<FinnhubOptions> options, ILogger<FinnhubSectorFeeder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _retryPolicy = FeederRetryPolicy.Build("FINNHUB-SECTOR", logger);
    }

    public SectorSourceType Source => SectorSourceType.Finnhub;

    public async Task<SectorFetchResult> FetchSectorAsync(string ticker, CancellationToken cancellationToken)
    {
        // Checked before any HTTP call is attempted, same precedent as
        // FinnhubExDividendFeeder - a missing credential must throw distinctly rather than
        // being converted into a generic Success = false result indistinguishable from a
        // real source-side rejection. Same "Finnhub" config section/shared secrets file.
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ConfigurationMissingException("Finnhub:ApiKey");

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var url = $"{_options.BaseUrl}/stock/profile2?symbol={Uri.EscapeDataString(ticker)}";

            var response = await _retryPolicy.ExecuteAsync(async token =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-Finnhub-Token", _options.ApiKey);
                return await client.SendAsync(request, token);
            }, cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(json);

            // Finnhub's /stock/profile2 returns a bare top-level JSON object. Anything else
            // is an unexpected response shape (contract drift), not a legitimate result.
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new SectorFetchResult
                {
                    Success = false,
                    Observations = Array.Empty<RawSectorObservation>(),
                    ErrorMessage = "Finnhub response was not a JSON object (unexpected response shape)"
                };
            }

            // An unrecognized ticker returns "{}" (200 OK, no finnhubIndustry property) -
            // same ambiguous shape as a real company Finnhub simply has no industry
            // classification for. Neither case is distinguishable from the response alone.
            if (!doc.RootElement.TryGetProperty("finnhubIndustry", out var industryElement)
                || industryElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(industryElement.GetString()))
            {
                return new SectorFetchResult { Success = true, Observations = Array.Empty<RawSectorObservation>(), NoDataForRange = true };
            }

            // Primary source, n=1 by design - Verified = true unconditionally, no
            // cross-verification attempted or required (per this entity's own doc comment).
            // SicCode is EDGAR-only and always null for a Finnhub row.
            var observation = new RawSectorObservation
            {
                Ticker = ticker,
                Source = SectorSourceType.Finnhub,
                SectorValue = industryElement.GetString(),
                SicCode = null,
                FetchedAt = DateTime.UtcNow,
                Verified = true
            };

            return new SectorFetchResult { Success = true, Observations = new[] { observation } };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FINNHUB-SECTOR-FEEDER]: fetch failed for {Ticker}", ticker);
            return new SectorFetchResult { Success = false, Observations = Array.Empty<RawSectorObservation>(), ErrorMessage = ex.Message };
        }
    }
}
