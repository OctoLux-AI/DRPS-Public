using System.Text.Json;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Microsoft.Extensions.Options;
using Polly.Retry;

namespace Drps.Ingestion.Feeders;

public class FinnhubExDividendFeeder : IExDividendFeeder
{
    public const string HttpClientName = "FinnhubExDividendClient";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FinnhubOptions _options;
    private readonly ILogger<FinnhubExDividendFeeder> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public FinnhubExDividendFeeder(IHttpClientFactory httpClientFactory, IOptions<FinnhubOptions> options, ILogger<FinnhubExDividendFeeder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _retryPolicy = FeederRetryPolicy.Build("FINNHUB-EXDIV", logger);
    }

    public SourceType Source => SourceType.Finnhub;

    public async Task<ExDividendFetchResult> FetchExDividendsAsync(string symbol, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        // Checked before any HTTP call is attempted (outside the try/catch below) so a
        // missing credential throws ConfigurationMissingException and propagates to the
        // caller distinctly, rather than being converted into a generic Success = false
        // result indistinguishable from a real source-side rejection. Reads from the same
        // "Finnhub" config section (and therefore the same shared secrets file) FinnhubClient
        // already expects - no new secrets location.
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ConfigurationMissingException("Finnhub:ApiKey");

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var url = $"{_options.BaseUrl}/stock/dividend?symbol={Uri.EscapeDataString(symbol)}" +
                      $"&from={start:yyyy-MM-dd}&to={end:yyyy-MM-dd}";

            var response = await _retryPolicy.ExecuteAsync(async token =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Add("X-Finnhub-Token", _options.ApiKey);
                return await client.SendAsync(request, token);
            }, cancellationToken);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            using var doc = JsonDocument.Parse(json);

            // Finnhub's /stock/dividend returns a bare top-level JSON array. Anything else
            // is an unexpected response shape (contract drift), not a legitimate result.
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new ExDividendFetchResult
                {
                    Success = false,
                    Observations = Array.Empty<RawExDividendObservation>(),
                    ErrorMessage = "Finnhub response was not a JSON array (unexpected response shape)"
                };
            }

            var observations = MapObservations(symbol, doc.RootElement);

            // Same ambiguity AlpacaFeeder/TiingoFeeder already carve out for bars: a
            // never-dividend-paying stock and an invalid ticker both produce this same
            // 200+"[]" shape, and there's no way to tell them apart from the response alone.
            return new ExDividendFetchResult { Success = true, Observations = observations, NoDataForRange = observations.Count == 0 };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FINNHUB-EXDIV-FEEDER]: fetch failed for {Symbol}", symbol);
            return new ExDividendFetchResult { Success = false, Observations = Array.Empty<RawExDividendObservation>(), ErrorMessage = ex.Message };
        }
    }

    private static IReadOnlyList<RawExDividendObservation> MapObservations(string symbol, JsonElement arrayElement)
    {
        var observations = new List<RawExDividendObservation>();
        var ingestedAt = DateTimeOffset.UtcNow;

        foreach (var entry in arrayElement.EnumerateArray())
        {
            // Deliberately reading "amount" (the declared, as-reported figure), never
            // "adjustedAmount" - same raw-vs-adjusted discipline CLAUDE.md requires for
            // OHLCV bars, applied consistently here even though this field isn't OHLCV.
            observations.Add(new RawExDividendObservation
            {
                Source = SourceType.Finnhub,
                Symbol = symbol,
                ExDividendDate = DateOnly.Parse(entry.GetProperty("date").GetString()!),
                Value = entry.GetProperty("amount").GetDecimal(),
                SampleCount = 1,
                VariancePct = null,
                Verified = false,
                IngestedAt = ingestedAt,
                RequestId = Guid.NewGuid()
            });
        }

        return observations;
    }
}
