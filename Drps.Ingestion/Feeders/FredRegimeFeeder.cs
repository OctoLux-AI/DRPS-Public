using System.Globalization;
using Drps.Shared.Models;
using Polly.Retry;

namespace Drps.Ingestion.Feeders;

/// <summary>
/// FRED's fredgraph.csv export (fred.stlouisfed.org/graph/fredgraph.csv?id={SERIES_ID}) -
/// the depth source for VXN/VIX3M, predating Cboe's own direct export by several years for
/// both series (CLAUDE.md's "Regime Data Sourcing" decision, 2026-07-26). Not an independent
/// measurement of VXN/VIX3M - FRED redistributes Cboe's own already-published number (see
/// RegimeSourceType's own doc comment), so this feeder performs no comparison against
/// CboeRegimeFeeder's output; both are simply persisted, distinguished by Source.
///
/// Confirmed empirically against both real series (2026-07-26): header
/// "observation_date,{SERIES_ID}", date as yyyy-MM-dd, one row per business day INCLUDING
/// market holidays - a holiday row carries a real date with nothing after the trailing comma
/// (e.g. "2001-02-19,"), not a numeric value and not FRED's "." missing-data placeholder used
/// elsewhere. Confirmed 241 such rows across VXNCLS's full 2001-2026 history and 176 across
/// VXVCLS's shorter 2007-2026 history (~9-11/year in both cases) - skipped during parse,
/// never stored as a null-Close row and never treated as a parse error. A "." value is also
/// treated as missing, defensively, even though it was not observed in either real series
/// tested.
///
/// FRED requests are slow and can hang or silently truncate under a short client timeout -
/// originally confirmed via a plain HTTP GET/curl.exe spot-check (2026-07-26), and since
/// hardened much further: CLAUDE.md's "Production FredRegimeFeeder curl.exe Fix" (2026-07-28)
/// found this feeder's own HttpClient-based path failed 4 of 8 live trials against its real
/// production code (each burning ~414s exhausting all 3 Polly retries before giving up),
/// confirming the 2026-07-27 VIX3M/FRED transport failure was not a one-off. Fetching now goes
/// through <see cref="IFredCsvTransport"/> (curl.exe in production, see
/// <see cref="CurlFredCsvTransport"/>'s own doc comment) rather than HttpClient - this feeder
/// no longer resolves an IHttpClientFactory at all.
/// </summary>
public class FredRegimeFeeder : IRegimeFeeder
{
    public const string VxnUrl = "https://fred.stlouisfed.org/graph/fredgraph.csv?id=VXNCLS";
    public const string Vix3mUrl = "https://fred.stlouisfed.org/graph/fredgraph.csv?id=VXVCLS";

    private readonly IFredCsvTransport _transport;
    private readonly string _csvUrl;
    private readonly string _seriesId;
    private readonly ILogger<FredRegimeFeeder> _logger;
    private readonly AsyncRetryPolicy _retryPolicy;

    public FredRegimeFeeder(
        IFredCsvTransport transport, string ticker, string csvUrl, string seriesId, ILogger<FredRegimeFeeder> logger)
    {
        _transport = transport;
        Ticker = ticker;
        _csvUrl = csvUrl;
        _seriesId = seriesId;
        _logger = logger;
        _retryPolicy = FeederRetryPolicy.BuildForCurlFailures($"{ticker}-FRED", logger);
    }

    public string Ticker { get; }

    public RegimeSourceType Source => RegimeSourceType.Fred;

    public async Task<RegimeFetchResult> FetchHistoryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var csv = await _retryPolicy.ExecuteAsync(
                token => _transport.FetchCsvAsync(_csvUrl, token), cancellationToken);

            var lines = csv.Split('\n');

            var expectedHeader = $"observation_date,{_seriesId}";
            if (lines.Length == 0 || !lines[0].Trim().Equals(expectedHeader, StringComparison.Ordinal))
            {
                return new RegimeFetchResult
                {
                    Success = false,
                    ErrorMessage = $"FRED {Ticker} response did not start with the expected header (unexpected response shape)"
                };
            }

            var observations = new List<RawRegimeObservation>();
            var holidayCount = 0;
            var malformedCount = 0;
            var fetchedAt = DateTime.UtcNow;

            for (var i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;

                var fields = line.Split(',');
                if (fields.Length != 2)
                {
                    malformedCount++;
                    _logger.LogWarning("[FRED-REGIME-FEEDER/{Ticker}]: skipping malformed row: {Line}", Ticker, line);
                    continue;
                }

                var valueField = fields[1].Trim();
                if (string.IsNullOrWhiteSpace(valueField) || valueField == ".")
                {
                    // A calendar holiday - FRED still emits a dated row with nothing after the
                    // value column. Legitimate, expected, and common (~9-11/year) - not a parse
                    // error and never stored as a null-Close row.
                    holidayCount++;
                    continue;
                }

                try
                {
                    var date = DateOnly.ParseExact(fields[0].Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var close = decimal.Parse(valueField, NumberStyles.Number, CultureInfo.InvariantCulture);

                    observations.Add(new RawRegimeObservation
                    {
                        Ticker = Ticker,
                        ObservationDate = date,
                        Open = null,
                        High = null,
                        Low = null,
                        Close = close,
                        Source = RegimeSourceType.Fred,
                        FetchedAt = fetchedAt
                    });
                }
                catch (FormatException ex)
                {
                    malformedCount++;
                    _logger.LogWarning(ex, "[FRED-REGIME-FEEDER/{Ticker}]: skipping unparseable row: {Line}", Ticker, line);
                }
            }

            _logger.LogInformation(
                "[FRED-REGIME-FEEDER/{Ticker}]: parsed {ObservationCount} observation(s), skipped {HolidayCount} holiday row(s), " +
                "{MalformedCount} malformed row(s)",
                Ticker, observations.Count, holidayCount, malformedCount);

            return new RegimeFetchResult { Success = true, Observations = observations };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FRED-REGIME-FEEDER/{Ticker}]: fetch failed", Ticker);
            return new RegimeFetchResult { Success = false, ErrorMessage = ex.Message };
        }
    }
}
