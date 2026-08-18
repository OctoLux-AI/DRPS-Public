using System.Text.Json;
using Drps.Ingestion.Persistence;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Microsoft.Extensions.Options;
using Polly.Retry;

namespace Drps.Ingestion.Feeders;

// Deliberately self-persisting, unlike FinnhubSectorFeeder/FinnhubExDividendFeeder (which
// return a Result for a Runner to persist, and which deliberately never persist an ambiguous
// "no data" or failed fetch at all). The Earnings Blackout Gate Decision (CLAUDE.md,
// 2026-07-19) is fail-closed: a Gate consumer needs to distinguish "we tried and the fetch
// failed" (an explicit Verified=false row) from "we haven't looked yet" (no row at all) -
// an absent row would be indistinguishable from a candidate simply never scanned, which
// would defeat the fail-closed design. So every call writes exactly one row, success or
// failure, and never throws past a missing API key (the one case that means no HTTP call
// was ever attempted at all - same ConfigurationMissingException precedent as every other
// Finnhub feeder in this project).
public class FinnhubEarningsFeeder
{
    public const string HttpClientName = "FinnhubEarningsClient";

    // Matches the Earnings Blackout Gate Decision's stated 7-day lookahead window.
    private const int LookaheadDays = 7;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FinnhubOptions _options;
    private readonly DrpsDbContext _dbContext;
    private readonly ILogger<FinnhubEarningsFeeder> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public FinnhubEarningsFeeder(
        IHttpClientFactory httpClientFactory,
        IOptions<FinnhubOptions> options,
        DrpsDbContext dbContext,
        ILogger<FinnhubEarningsFeeder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _dbContext = dbContext;
        _logger = logger;
        _retryPolicy = FeederRetryPolicy.Build("FINNHUB-EARNINGS", logger);
    }

    public SourceType Source => SourceType.Finnhub;

    public async Task<RawEarningsObservation> FetchNextEarningsAsync(string ticker, CancellationToken cancellationToken)
    {
        // Checked before any HTTP call is attempted, same precedent as FinnhubSectorFeeder/
        // FinnhubExDividendFeeder - a missing credential is a client-side configuration bug,
        // not a source failure, so it throws distinctly rather than persisting a false
        // "the source failed" row.
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ConfigurationMissingException("Finnhub:ApiKey");

        DateOnly? nextEarningsDate = null;
        var fetchOutcome = EarningsFetchOutcome.Unknown;

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            var start = DateOnly.FromDateTime(DateTime.UtcNow);
            var end = start.AddDays(LookaheadDays);
            var url = $"{_options.BaseUrl}/calendar/earnings?symbol={Uri.EscapeDataString(ticker)}" +
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

            var extraction = ExtractEarliestDate(doc.RootElement, start);
            fetchOutcome = extraction.Outcome;
            nextEarningsDate = extraction.EarliestDate;

            if (fetchOutcome == EarningsFetchOutcome.Unknown)
            {
                _logger.LogWarning(
                    "[FINNHUB-EARNINGS-FEEDER]: no parseable earnings date found for {Ticker} in response", ticker);
            }
            else if (fetchOutcome == EarningsFetchOutcome.NoUpcomingEarningsInWindow)
            {
                // Deliberately NOT a Warning - this is the correctly-distinguished, safe
                // counterpart to the branch above (CLAUDE.md's "Earnings Verification
                // Tri-State Fix," 2026-08-07). Logged at Information for basic visibility
                // only, not because it's noteworthy.
                _logger.LogInformation(
                    "[FINNHUB-EARNINGS-FEEDER]: no upcoming earnings within the lookahead window for {Ticker}", ticker);
            }
        }
        catch (Exception ex)
        {
            // Fetch failure, non-2xx after retries exhausted, or a malformed response body -
            // all collapse to the same fail-closed outcome: FetchOutcome=Unknown, no date.
            // Never rethrown past this point, per this feeder's own fail-closed contract - a
            // missing/failed fetch must leave an explicit Unknown row behind, not silence or
            // an unhandled exception.
            _logger.LogError(ex, "[FINNHUB-EARNINGS-FEEDER]: fetch failed for {Ticker}", ticker);
            nextEarningsDate = null;
            fetchOutcome = EarningsFetchOutcome.Unknown;
        }

        var observation = new RawEarningsObservation
        {
            Ticker = ticker,
            Source = SourceType.Finnhub,
            NextEarningsDate = nextEarningsDate,
            FetchedAt = DateTime.UtcNow,
            FetchOutcome = fetchOutcome
        };

        _dbContext.RawEarningsObservations.Add(observation);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return observation;
    }

    private readonly record struct EarningsExtractionResult(EarningsFetchOutcome Outcome, DateOnly? EarliestDate);

    // Finnhub's /calendar/earnings returns {"earningsCalendar": [{"date": "...", ...}, ...]}.
    // Takes the earliest parseable, in-window date found rather than assuming the array's
    // returned order is already sorted - "next earnings date" means chronologically
    // nearest, not "whichever entry happened to come first in the response."
    //
    // Three outcomes, not a bare nullable date (CLAUDE.md's "Earnings Verification
    // Tri-State Fix," 2026-08-07):
    //   - Unknown: the top-level response shape itself is wrong (not an object, no
    //     earningsCalendar property, or earningsCalendar isn't an array) - a genuine
    //     parsing failure.
    //   - NoUpcomingEarningsInWindow: earningsCalendar IS a well-formed array, but nothing
    //     in it survives filtering - whether because it's genuinely empty, every entry is
    //     individually malformed (no "date" field, unparseable date), or every parseable
    //     date is < start. All three collapse to the same safe "nothing upcoming" result -
    //     a malformed individual entry among otherwise-good ones was never itself a
    //     top-level parsing failure, and is treated identically here to a genuinely absent
    //     one, matching this method's original stated intent.
    //   - UpcomingEarningsFound: at least one entry parses to a date >= start.
    //
    // The `parsedDate < start` filter is the fix for the 2026-08-07 audit's same-day-edge-
    // case finding: without it, a company that already reported earlier the same calendar
    // day as `start` could have its already-elapsed date treated as "the next" one, since
    // the prior version took the minimum date across every returned entry with no lower
    // bound at all. `>=` is intentionally inclusive of `start` itself - Finnhub's dates
    // carry no time-of-day certainty, so a same-day entry may genuinely not have reported
    // yet (e.g. an "amc" - after market close - report).
    private static EarningsExtractionResult ExtractEarliestDate(JsonElement root, DateOnly start)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("earningsCalendar", out var calendar)
            || calendar.ValueKind != JsonValueKind.Array)
        {
            return new EarningsExtractionResult(EarningsFetchOutcome.Unknown, null);
        }

        DateOnly? earliest = null;

        foreach (var entry in calendar.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("date", out var dateElement)
                || dateElement.ValueKind != JsonValueKind.String
                || !DateOnly.TryParse(dateElement.GetString(), out var parsedDate)
                || parsedDate < start)
            {
                continue;
            }

            if (earliest is null || parsedDate < earliest.Value)
                earliest = parsedDate;
        }

        return earliest is null
            ? new EarningsExtractionResult(EarningsFetchOutcome.NoUpcomingEarningsInWindow, null)
            : new EarningsExtractionResult(EarningsFetchOutcome.UpcomingEarningsFound, earliest);
    }
}
