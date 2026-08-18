using System.Globalization;
using Drps.Shared.Models;
using Polly.Retry;

namespace Drps.Ingestion.Feeders;

/// <summary>
/// Cboe's own direct CSV export (cdn.cboe.com/.../daily_prices/{TICKER}_History.csv) - the
/// sole authoritative source for VIX/VXN/VIX3M (CLAUDE.md's "Regime Data Sourcing" decision,
/// 2026-07-26). Format confirmed empirically against all three real endpoints (2026-07-26):
/// header "DATE,OPEN,HIGH,LOW,CLOSE", one row per trading day, date as MM/dd/yyyy, no
/// holiday/blank rows (unlike FRED's export - see FredRegimeFeeder's own doc comment). Full-
/// history pull on every call, not a date-ranged request - Cboe's export has no query-string
/// date-range parameter.
/// </summary>
public class CboeRegimeFeeder : IRegimeFeeder
{
    public const string HttpClientName = "CboeRegimeClient";

    public const string VixUrl = "https://cdn.cboe.com/api/global/us_indices/daily_prices/VIX_History.csv";
    public const string VxnUrl = "https://cdn.cboe.com/api/global/us_indices/daily_prices/VXN_History.csv";
    public const string Vix3mUrl = "https://cdn.cboe.com/api/global/us_indices/daily_prices/VIX3M_History.csv";

    private const string ExpectedHeader = "DATE,OPEN,HIGH,LOW,CLOSE";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _csvUrl;
    private readonly ILogger<CboeRegimeFeeder> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public CboeRegimeFeeder(IHttpClientFactory httpClientFactory, string ticker, string csvUrl, ILogger<CboeRegimeFeeder> logger)
    {
        _httpClientFactory = httpClientFactory;
        Ticker = ticker;
        _csvUrl = csvUrl;
        _logger = logger;
        _retryPolicy = FeederRetryPolicy.Build($"{ticker}-CBOE", logger);
    }

    public string Ticker { get; }

    public RegimeSourceType Source => RegimeSourceType.CboeDirect;

    public async Task<RegimeFetchResult> FetchHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            var response = await _retryPolicy.ExecuteAsync(
                token => client.GetAsync(_csvUrl, token), cancellationToken);

            response.EnsureSuccessStatusCode();

            var csv = await response.Content.ReadAsStringAsync(cancellationToken);
            var lines = csv.Split('\n');

            // Contract-drift check, same shape as every JSON feeder's "unexpected response
            // shape" guard elsewhere in this codebase - a header mismatch means Cboe changed
            // its export format, not a legitimate (if unusual) data row.
            if (lines.Length == 0 || !lines[0].Trim().Equals(ExpectedHeader, StringComparison.Ordinal))
            {
                return new RegimeFetchResult
                {
                    Success = false,
                    ErrorMessage = $"Cboe {Ticker} response did not start with the expected header (unexpected response shape)"
                };
            }

            var observations = new List<RawRegimeObservation>();
            var skippedCount = 0;
            var fetchedAt = DateTime.UtcNow;

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;

                var fields = line.Split(',');
                if (fields.Length != 5)
                {
                    skippedCount++;
                    _logger.LogWarning("[CBOE-REGIME-FEEDER/{Ticker}]: skipping malformed row: {Line}", Ticker, line);
                    continue;
                }

                try
                {
                    var date = DateOnly.ParseExact(fields[0].Trim(), "MM/dd/yyyy", CultureInfo.InvariantCulture);
                    var open = decimal.Parse(fields[1], NumberStyles.Number, CultureInfo.InvariantCulture);
                    var high = decimal.Parse(fields[2], NumberStyles.Number, CultureInfo.InvariantCulture);
                    var low = decimal.Parse(fields[3], NumberStyles.Number, CultureInfo.InvariantCulture);
                    var close = decimal.Parse(fields[4], NumberStyles.Number, CultureInfo.InvariantCulture);

                    observations.Add(new RawRegimeObservation
                    {
                        Ticker = Ticker,
                        ObservationDate = date,
                        Open = open,
                        High = high,
                        Low = low,
                        Close = close,
                        Source = RegimeSourceType.CboeDirect,
                        FetchedAt = fetchedAt
                    });
                }
                catch (FormatException ex)
                {
                    skippedCount++;
                    _logger.LogWarning(ex, "[CBOE-REGIME-FEEDER/{Ticker}]: skipping unparseable row: {Line}", Ticker, line);
                }
            }

            if (skippedCount > 0)
            {
                _logger.LogWarning(
                    "[CBOE-REGIME-FEEDER/{Ticker}]: skipped {SkippedCount} malformed/unparseable row(s) out of {TotalDataLines} data row(s)",
                    Ticker, skippedCount, lines.Length - 1);
            }

            return new RegimeFetchResult { Success = true, Observations = observations };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CBOE-REGIME-FEEDER/{Ticker}]: fetch failed", Ticker);
            return new RegimeFetchResult { Success = false, ErrorMessage = ex.Message };
        }
    }
}
