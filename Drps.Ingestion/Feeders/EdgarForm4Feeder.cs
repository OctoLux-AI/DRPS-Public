using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.Extensions.Options;
using Polly.Retry;

namespace Drps.Ingestion.Feeders;

/// <summary>
/// Fetches individual Form 4 purchase transactions from SEC EDGAR (Insider Form 4 Adjuster
/// Multiplier Decision, CLAUDE.md 2026-07-19) - purchase-side only (transactionCode == "P"),
/// no insider-selling penalty logic, matching CapitalFill's own scope.
///
/// Reuses <see cref="SecEdgarCikResolver"/> directly rather than duplicating ticker->CIK
/// resolution - the same component <see cref="SecEdgarSectorFeeder"/> already depends on.
/// Also reuses <see cref="SecEdgarSectorFeeder.HttpClientName"/> for the submissions call
/// (identical endpoint shape, {SubmissionsBaseUrl}/CIK{cik}.json - no reason for a second,
/// differently-named registration of the same client) and <see cref="SecEdgarRateLimiter"/>
/// for the same proactive self-throttling every SEC EDGAR feeder in this codebase shares.
///
/// Deliberately self-persisting, same shape as FinnhubEarningsFeeder - but unlike that
/// feeder (exactly one scalar value per ticker per call), a ticker can have zero, one, or
/// many purchase transactions in a single call. The "never silently no-op" rule applies to
/// every outcome, not just genuine failure: CIK resolution failing, the submissions
/// fetch/parse failing, or an individual filing's XML failing to parse each persist exactly
/// one Verified=false row; a scan that completes cleanly but finds zero "P" transactions
/// (no Form 4 filings in the trailing window, or filings exist but contain none) persists
/// exactly one Verified=true, zero-value marker row instead. Both are real, distinct facts
/// worth recording - "we tried and failed" is not the same as "we checked and it's clean,"
/// and collapsing either one into silent non-persistence would make it indistinguishable
/// from a ticker that was never scanned at all (see InsiderLookupService's own doc comment
/// for why that distinction matters downstream).
/// </summary>
public class EdgarForm4Feeder
{
    public const string ArchivesHttpClientName = "SecEdgarArchivesClient";

    // Insider Form 4 Adjuster Multiplier Decision's stated window - explicitly an
    // uncalibrated placeholder (Kent's correction from CapitalFill's 90-day precedent), same
    // category as every other "needs real data to tune" numeric placeholder in this codebase.
    private const int WindowDays = 60;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SecEdgarCikResolver _cikResolver;
    private readonly SecEdgarRateLimiter _rateLimiter;
    private readonly SecEdgarOptions _options;
    private readonly DrpsDbContext _dbContext;
    private readonly ILogger<EdgarForm4Feeder> _logger;
    private readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    public EdgarForm4Feeder(
        IHttpClientFactory httpClientFactory,
        SecEdgarCikResolver cikResolver,
        SecEdgarRateLimiter rateLimiter,
        IOptions<SecEdgarOptions> options,
        DrpsDbContext dbContext,
        ILogger<EdgarForm4Feeder> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cikResolver = cikResolver;
        _rateLimiter = rateLimiter;
        _options = options.Value;
        _dbContext = dbContext;
        _logger = logger;
        _retryPolicy = FeederRetryPolicy.Build("SEC-EDGAR-FORM4", logger);
    }

    public SourceType Source => SourceType.SecEdgarForm4;

    public async Task<IReadOnlyList<RawInsiderObservation>> FetchInsiderPurchasesAsync(string ticker, CancellationToken cancellationToken)
    {
        var toPersist = new List<RawInsiderObservation>();

        var cik = await _cikResolver.ResolveCikAsync(ticker, cancellationToken);
        if (cik is null)
        {
            // Same ambiguous-"cannot resolve" shape SecEdgarSectorFeeder already treats as
            // legitimate (unrecognized ticker, or the ticker-map fetch itself failed) - but
            // this feeder's fail-closed contract has no separate "no data" outcome to fall
            // back to, so it collapses to the same unverified row every other failure below
            // produces. No submissions call is even attempted.
            _logger.LogWarning("[SEC-EDGAR-FORM4-FEEDER]: no CIK mapping found for {Ticker} - persisting unverified row", ticker);
            toPersist.Add(BuildUnverifiedObservation(ticker));
        }
        else
        {
            IReadOnlyList<Form4FilingReference>? filings = null;
            try
            {
                filings = await FetchFilingsInWindowAsync(cik, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[SEC-EDGAR-FORM4-FEEDER]: submissions fetch/parse failed for {Ticker}", ticker);
                toPersist.Add(BuildUnverifiedObservation(ticker));
            }

            if (filings is not null)
            {
                var anyFilingFailed = false;

                foreach (var filing in filings)
                {
                    // One filing's failure must never stop the rest of the batch - same
                    // per-item isolation principle used throughout this codebase's
                    // orchestration layer (IngestionRunner, GateScanService's candidate loop).
                    try
                    {
                        var purchases = await FetchAndParseFilingAsync(cik, filing, cancellationToken);
                        foreach (var purchase in purchases)
                        {
                            toPersist.Add(new RawInsiderObservation
                            {
                                Ticker = ticker,
                                Source = SourceType.SecEdgarForm4,
                                TransactionDate = purchase.TransactionDate,
                                DollarValue = purchase.Shares * purchase.PricePerShare,
                                InsiderName = purchase.InsiderName,
                                FetchedAt = DateTime.UtcNow,
                                Verified = true
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        anyFilingFailed = true;
                        _logger.LogError(
                            ex, "[SEC-EDGAR-FORM4-FEEDER]: filing fetch/parse failed for {Ticker}, accession {AccessionNumber}",
                            ticker, filing.AccessionNumber);
                    }
                }

                // One unverified row for the whole ticker-level batch, not one per failed
                // filing - a single, honest "something in this run could not be parsed"
                // signal, alongside whatever purchase rows the other, successfully-parsed
                // filings in the same batch already contributed above.
                if (anyFilingFailed)
                {
                    toPersist.Add(BuildUnverifiedObservation(ticker));
                }
                else if (toPersist.Count == 0)
                {
                    // The scan genuinely completed - CIK resolved, submissions fetched, every
                    // filing in the window parsed without error - and simply found zero "P"
                    // transactions (no Form 4 filings in the window, or filings existed but
                    // none were purchases). A real, confirmed-clean result, not a failure and
                    // not silence: persisted as its own Verified=true marker so a downstream
                    // reader (InsiderLookupService) can tell "we checked, it's clean" apart
                    // from "never scanned at all."
                    toPersist.Add(BuildScannedCleanObservation(ticker));
                }
            }
        }

        if (toPersist.Count > 0)
        {
            _dbContext.RawInsiderObservations.AddRange(toPersist);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return toPersist;
    }

    // DollarValue's column is a non-nullable decimal(18,4) (RawInsiderObservation's own
    // shape) - 0m is the fail-closed sentinel here, never a real computed value, and is only
    // ever meaningful paired with Verified=false so a reader never mistakes it for a
    // genuine zero-dollar transaction.
    private static RawInsiderObservation BuildUnverifiedObservation(string ticker) => new()
    {
        Ticker = ticker,
        Source = SourceType.SecEdgarForm4,
        TransactionDate = DateOnly.FromDateTime(DateTime.UtcNow),
        DollarValue = 0m,
        InsiderName = null,
        FetchedAt = DateTime.UtcNow,
        Verified = false
    };

    // Distinct from BuildUnverifiedObservation above despite the identical DollarValue=0m -
    // Verified=true here, since this represents a scan that genuinely completed and found
    // nothing, not one that couldn't complete at all. TransactionDate="today" is a real
    // statement here too ("as of today, nothing found"), same as the failure sentinel's use
    // of it, not a placeholder standing in for an unknown date.
    private static RawInsiderObservation BuildScannedCleanObservation(string ticker) => new()
    {
        Ticker = ticker,
        Source = SourceType.SecEdgarForm4,
        TransactionDate = DateOnly.FromDateTime(DateTime.UtcNow),
        DollarValue = 0m,
        InsiderName = null,
        FetchedAt = DateTime.UtcNow,
        Verified = true
    };

    private async Task<IReadOnlyList<Form4FilingReference>> FetchFilingsInWindowAsync(string cik, CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitForSlotAsync(cancellationToken);

        var client = _httpClientFactory.CreateClient(SecEdgarSectorFeeder.HttpClientName);
        var url = $"{_options.SubmissionsBaseUrl}/CIK{cik}.json";

        var response = await _retryPolicy.ExecuteAsync(async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            return await client.SendAsync(request, token);
        }, cancellationToken);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        var windowEnd = DateOnly.FromDateTime(DateTime.UtcNow);
        var windowStart = windowEnd.AddDays(-WindowDays);

        // Throws on a genuinely malformed/unexpected-shape response - propagates to this
        // method's own caller, which treats it identically to an HTTP-level fetch failure
        // (the response came back but was unusable is still a submissions failure).
        return ParseForm4FilingsInWindow(json, windowStart, windowEnd);
    }

    private async Task<IReadOnlyList<Form4PurchaseTransaction>> FetchAndParseFilingAsync(
        string cik, Form4FilingReference filing, CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitForSlotAsync(cancellationToken);

        var client = _httpClientFactory.CreateClient(ArchivesHttpClientName);
        var url = BuildArchiveUrl(cik, filing);

        var response = await _retryPolicy.ExecuteAsync(async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            return await client.SendAsync(request, token);
        }, cancellationToken);

        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync(cancellationToken);

        // Throws on genuinely malformed XML (XDocument.Parse) - propagates to this method's
        // caller, which treats it as this one filing's failure without touching the rest of
        // the batch.
        return ParsePurchaseTransactions(xml);
    }

    // {ArchivesBaseUrl}/{cik, leading zeros stripped}/{accession number, dashes stripped}/{primaryDocument}
    // - the standard EDGAR archive path shape for an individual filing's documents.
    private string BuildArchiveUrl(string cik, Form4FilingReference filing)
    {
        var cikNoLeadingZeros = cik.TrimStart('0');
        if (cikNoLeadingZeros.Length == 0)
            cikNoLeadingZeros = "0";

        var accessionNoDashes = filing.AccessionNumber.Replace("-", "");

        return $"{_options.ArchivesBaseUrl}/{cikNoLeadingZeros}/{accessionNoDashes}/{filing.PrimaryDocument}";
    }

    // Public, not private - exercised directly by unit tests against fixture payloads
    // without needing to fake an HttpClient round-trip, same "public static helper, testable
    // directly" precedent as SecEdgarCikResolver.ParseTickerMap. Throws (rather than
    // returning an empty list) when the response isn't even shaped like a submissions
    // payload at all - see this method's caller for why that distinction matters
    // (unexpected shape is a fetch-level failure; a well-shaped-but-empty/filtered result is
    // the legitimate "zero filings in this window" outcome and must NOT throw).
    public static IReadOnlyList<Form4FilingReference> ParseForm4FilingsInWindow(string json, DateOnly windowStart, DateOnly windowEnd)
    {
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("filings", out var filingsElement)
            || filingsElement.ValueKind != JsonValueKind.Object
            || !filingsElement.TryGetProperty("recent", out var recentElement)
            || recentElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("SEC EDGAR submissions response was not in the expected filings.recent shape");
        }

        var forms = ReadStringArray(recentElement, "form");
        var filingDates = ReadStringArray(recentElement, "filingDate");
        var accessionNumbers = ReadStringArray(recentElement, "accessionNumber");
        var primaryDocuments = ReadStringArray(recentElement, "primaryDocument");

        // SEC's parallel arrays are always kept in lockstep in practice, but this guards
        // against a truncated/malformed one silently causing an index-out-of-range instead
        // of just processing whatever entries all four arrays actually agree on.
        var count = new[] { forms.Count, filingDates.Count, accessionNumbers.Count, primaryDocuments.Count }.Min();

        var results = new List<Form4FilingReference>();
        for (var i = 0; i < count; i++)
        {
            if (forms[i] != "4")
                continue;

            if (!DateOnly.TryParse(filingDates[i], CultureInfo.InvariantCulture, out var filingDate))
                continue;

            if (filingDate < windowStart || filingDate > windowEnd)
                continue;

            results.Add(new Form4FilingReference(accessionNumbers[i], primaryDocuments[i], filingDate));
        }

        return results;
    }

    private static List<string> ReadStringArray(JsonElement parent, string propertyName)
    {
        var result = new List<string>();

        if (parent.TryGetProperty(propertyName, out var arrayElement) && arrayElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arrayElement.EnumerateArray())
            {
                result.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : string.Empty);
            }
        }

        return result;
    }

    // Public, not private - same testability precedent as ParseForm4FilingsInWindow above.
    // Real Form 4 ownership XML carries no XML namespace, but every lookup here matches by
    // local name only regardless, so a namespaced variant would still parse correctly. Only
    // transactionCode == "P" entries produce a result - every other code (S, A, F, etc.) is
    // silently skipped, per this feeder's purchase-only scope. An individual
    // nonDerivativeTransaction element with unparseable date/shares/price is also skipped
    // (not thrown) - one bad line item among otherwise-good ones in the same filing
    // shouldn't discard the whole filing, same "skip the one bad entry" precedent
    // FinnhubEarningsFeeder's ExtractEarliestDate already establishes. Throws only when the
    // XML itself is not well-formed at all (XDocument.Parse) - that is this filing's genuine
    // parse failure, left to the caller to catch.
    public static IReadOnlyList<Form4PurchaseTransaction> ParsePurchaseTransactions(string xml)
    {
        var doc = XDocument.Parse(xml);
        var results = new List<Form4PurchaseTransaction>();

        if (doc.Root is null)
            return results;

        var insiderName = doc.Root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "rptOwnerName")?.Value;

        var transactions = doc.Root.Descendants()
            .Where(e => e.Name.LocalName == "nonDerivativeTransaction");

        foreach (var transaction in transactions)
        {
            var code = transaction.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "transactionCode")?.Value;

            if (code != "P")
                continue;

            var dateText = ReadNestedValue(transaction, "transactionDate");
            var sharesText = ReadNestedValue(transaction, "transactionShares");
            var priceText = ReadNestedValue(transaction, "transactionPricePerShare");

            if (!DateOnly.TryParse(dateText, CultureInfo.InvariantCulture, out var transactionDate)
                || !decimal.TryParse(sharesText, NumberStyles.Number, CultureInfo.InvariantCulture, out var shares)
                || !decimal.TryParse(priceText, NumberStyles.Number, CultureInfo.InvariantCulture, out var pricePerShare))
            {
                continue;
            }

            results.Add(new Form4PurchaseTransaction(transactionDate, shares, pricePerShare, insiderName));
        }

        return results;
    }

    // Form 4's XML wraps most scalar fields as <fieldName><value>...</value></fieldName> -
    // this reads that inner <value> by the outer field's local name.
    private static string? ReadNestedValue(XElement parent, string fieldLocalName)
    {
        return parent.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == fieldLocalName)?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "value")?.Value;
    }
}
