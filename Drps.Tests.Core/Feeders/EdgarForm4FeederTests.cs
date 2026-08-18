using System.Net;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Feeders;

public class EdgarForm4FeederTests
{
    private const string TickerMapFixture = """
    {
        "0": {"cik_str": 320193, "ticker": "AAPL", "title": "Apple Inc"}
    }
    """;

    private const string Filing1Xml = """
    <ownershipDocument>
        <reportingOwner>
            <reportingOwnerId>
                <rptOwnerName>DOE JOHN</rptOwnerName>
            </reportingOwnerId>
        </reportingOwner>
        <nonDerivativeTable>
            <nonDerivativeTransaction>
                <transactionDate><value>2026-07-10</value></transactionDate>
                <transactionCoding><transactionCode>P</transactionCode></transactionCoding>
                <transactionAmounts>
                    <transactionShares><value>1000</value></transactionShares>
                    <transactionPricePerShare><value>150.25</value></transactionPricePerShare>
                </transactionAmounts>
            </nonDerivativeTransaction>
        </nonDerivativeTable>
    </ownershipDocument>
    """;

    // Mixed codes in one filing - the "S" (sale) entry must never produce a row, only the
    // "P" entry.
    private const string Filing2Xml = """
    <ownershipDocument>
        <reportingOwner>
            <reportingOwnerId>
                <rptOwnerName>SMITH JANE</rptOwnerName>
            </reportingOwnerId>
        </reportingOwner>
        <nonDerivativeTable>
            <nonDerivativeTransaction>
                <transactionDate><value>2026-07-15</value></transactionDate>
                <transactionCoding><transactionCode>S</transactionCode></transactionCoding>
                <transactionAmounts>
                    <transactionShares><value>500</value></transactionShares>
                    <transactionPricePerShare><value>151.00</value></transactionPricePerShare>
                </transactionAmounts>
            </nonDerivativeTransaction>
            <nonDerivativeTransaction>
                <transactionDate><value>2026-07-15</value></transactionDate>
                <transactionCoding><transactionCode>P</transactionCode></transactionCoding>
                <transactionAmounts>
                    <transactionShares><value>200</value></transactionShares>
                    <transactionPricePerShare><value>152.50</value></transactionPricePerShare>
                </transactionAmounts>
            </nonDerivativeTransaction>
        </nonDerivativeTable>
    </ownershipDocument>
    """;

    // A well-formed filing containing only a non-"P" transaction - distinct from Filing2Xml
    // (which mixes an "S" alongside a real "P"), needed to exercise "a filing was found and
    // parsed successfully, but contributed zero purchases" as its own case, separate from
    // "zero filings existed in the window at all."
    private const string FilingOnlySaleXml = """
    <ownershipDocument>
        <reportingOwner>
            <reportingOwnerId>
                <rptOwnerName>DOE JOHN</rptOwnerName>
            </reportingOwnerId>
        </reportingOwner>
        <nonDerivativeTable>
            <nonDerivativeTransaction>
                <transactionDate><value>2026-07-12</value></transactionDate>
                <transactionCoding><transactionCode>S</transactionCode></transactionCoding>
                <transactionAmounts>
                    <transactionShares><value>500</value></transactionShares>
                    <transactionPricePerShare><value>151.00</value></transactionPricePerShare>
                </transactionAmounts>
            </nonDerivativeTransaction>
        </nonDerivativeTable>
    </ownershipDocument>
    """;

    private const string MalformedXml = "<ownershipDocument><unclosed>";

    private static string BuildSubmissionsFixture(params (string Form, DateTime FilingDate, string AccessionNumber, string PrimaryDocument)[] entries)
    {
        var forms = string.Join(",", entries.Select(e => $"\"{e.Form}\""));
        var dates = string.Join(",", entries.Select(e => $"\"{e.FilingDate:yyyy-MM-dd}\""));
        var accessions = string.Join(",", entries.Select(e => $"\"{e.AccessionNumber}\""));
        var docs = string.Join(",", entries.Select(e => $"\"{e.PrimaryDocument}\""));

        return $$"""
        {
            "cik": "320193",
            "filings": {
                "recent": {
                    "form": [{{forms}}],
                    "filingDate": [{{dates}}],
                    "accessionNumber": [{{accessions}}],
                    "primaryDocument": [{{docs}}]
                }
            }
        }
        """;
    }

    private static EdgarForm4Feeder CreateFeeder(
        FakeHttpMessageHandler handler, DrpsDbContext dbContext, SecEdgarOptions? options = null, SecEdgarCikResolver? cikResolver = null)
    {
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var resolvedOptions = Options.Create(options ?? new SecEdgarOptions { UserAgent = "DRPS/1.0 (contact: test@example.com)" });
        var resolver = cikResolver ?? new SecEdgarCikResolver(httpClientFactory, resolvedOptions, NullLogger<SecEdgarCikResolver>.Instance);
        return new EdgarForm4Feeder(
            httpClientFactory, resolver, new SecEdgarRateLimiter(), resolvedOptions, dbContext, NullLogger<EdgarForm4Feeder>.Instance);
    }

    // Routes by request URL so one fake handler can stand in for the ticker-map endpoint
    // (hit by the resolver), the submissions endpoint, and every individual filing document
    // fetch - mirrors SecEdgarSectorFeederTests' identical combined-handler pattern.
    private static FakeHttpMessageHandler CreateCombinedHandler(
        string submissionsJson,
        HttpStatusCode submissionsStatus = HttpStatusCode.OK,
        Dictionary<string, (HttpStatusCode Status, string Body)>? filingDocuments = null)
    {
        filingDocuments ??= new Dictionary<string, (HttpStatusCode, string)>();

        return new FakeHttpMessageHandler(request =>
        {
            var url = request.RequestUri!.ToString();

            if (url.Contains("company_tickers.json"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TickerMapFixture) };

            if (url.Contains("/submissions/"))
                return new HttpResponseMessage(submissionsStatus) { Content = new StringContent(submissionsJson) };

            var match = filingDocuments.Keys.FirstOrDefault(url.Contains);
            if (match is not null)
            {
                var (status, body) = filingDocuments[match];
                return new HttpResponseMessage(status) { Content = new StringContent(body) };
            }

            throw new InvalidOperationException($"Unexpected request URL in test: {url}");
        });
    }

    [Fact]
    public async Task FetchInsiderPurchasesAsync_MultipleFilingsWithPurchases_PersistsOneRowPerPurchaseTransactionAndIgnoresNonPCodes()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var submissionsJson = BuildSubmissionsFixture(
            ("4", DateTime.UtcNow.AddDays(-10), "0001234567-26-000001", "form4-1.xml"),
            ("10-Q", DateTime.UtcNow.AddDays(-20), "0001234567-26-000002", "10q.htm"),
            ("4", DateTime.UtcNow.AddDays(-3), "0001234567-26-000003", "form4-2.xml"));

        var handler = CreateCombinedHandler(submissionsJson, filingDocuments: new()
        {
            ["form4-1.xml"] = (HttpStatusCode.OK, Filing1Xml),
            ["form4-2.xml"] = (HttpStatusCode.OK, Filing2Xml)
        });
        var feeder = CreateFeeder(handler, dbContext);

        var result = await feeder.FetchInsiderPurchasesAsync("AAPL", CancellationToken.None);

        // Filing1 contributes its one "P" row; Filing2 contributes only its "P" row, never
        // its "S" row - three transactions parsed across both filings, two rows persisted.
        Assert.Equal(2, result.Count);
        Assert.All(result, o => Assert.True(o.Verified));
        Assert.All(result, o => Assert.Equal("AAPL", o.Ticker));
        Assert.All(result, o => Assert.Equal(SourceType.SecEdgarForm4, o.Source));

        var fromFiling1 = result.Single(o => o.TransactionDate == new DateOnly(2026, 7, 10));
        Assert.Equal(150250.00m, fromFiling1.DollarValue);
        Assert.Equal("DOE JOHN", fromFiling1.InsiderName);

        var fromFiling2 = result.Single(o => o.TransactionDate == new DateOnly(2026, 7, 15));
        Assert.Equal(30500.00m, fromFiling2.DollarValue);
        Assert.Equal("SMITH JANE", fromFiling2.InsiderName);

        Assert.Equal(2, await dbContext.RawInsiderObservations.CountAsync());
    }

    [Fact]
    public void ParsePurchaseTransactions_MixedTransactionCodesInOneFiling_OnlyReturnsPurchaseCode()
    {
        var purchases = EdgarForm4Feeder.ParsePurchaseTransactions(Filing2Xml);

        var purchase = Assert.Single(purchases);
        Assert.Equal(200m, purchase.Shares);
        Assert.Equal(152.50m, purchase.PricePerShare);
    }

    [Fact]
    public async Task FetchInsiderPurchasesAsync_CikResolutionFails_PersistsSingleUnverifiedRow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var handler = CreateCombinedHandler(BuildSubmissionsFixture());
        var feeder = CreateFeeder(handler, dbContext);

        var result = await feeder.FetchInsiderPurchasesAsync("ZZZZINVALID", CancellationToken.None);

        var observation = Assert.Single(result);
        Assert.Equal("ZZZZINVALID", observation.Ticker);
        Assert.False(observation.Verified);
        Assert.Equal(0m, observation.DollarValue);
        Assert.Null(observation.InsiderName);

        Assert.Single(await dbContext.RawInsiderObservations.ToListAsync());

        // No submissions request should ever have been attempted for an unresolvable ticker.
        Assert.DoesNotContain("submissions", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task FetchInsiderPurchasesAsync_SubmissionsFetchFails_PersistsSingleUnverifiedRow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        // Non-retryable status (404) so this test stays fast - retry-exhaustion timing is
        // already exercised elsewhere (e.g. FinnhubSectorFeederTests).
        var handler = CreateCombinedHandler("""{"error":"not found"}""", HttpStatusCode.NotFound);
        var feeder = CreateFeeder(handler, dbContext);

        var result = await feeder.FetchInsiderPurchasesAsync("AAPL", CancellationToken.None);

        var observation = Assert.Single(result);
        Assert.False(observation.Verified);
        Assert.Equal(0m, observation.DollarValue);

        Assert.Single(await dbContext.RawInsiderObservations.ToListAsync());
    }

    [Fact]
    public async Task FetchInsiderPurchasesAsync_SubmissionsResponseNotInExpectedShape_PersistsSingleUnverifiedRow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        // 200 OK but not the filings.recent shape at all - a fetch-level failure, distinct
        // from a legitimate empty/filtered result.
        var handler = CreateCombinedHandler("""{"cik": "320193"}""");
        var feeder = CreateFeeder(handler, dbContext);

        var result = await feeder.FetchInsiderPurchasesAsync("AAPL", CancellationToken.None);

        var observation = Assert.Single(result);
        Assert.False(observation.Verified);
    }

    [Fact]
    public async Task FetchInsiderPurchasesAsync_MalformedFilingXml_PersistsUnverifiedRowAndDoesNotCrashBatch()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var submissionsJson = BuildSubmissionsFixture(
            ("4", DateTime.UtcNow.AddDays(-10), "0001234567-26-000001", "form4-good.xml"),
            ("4", DateTime.UtcNow.AddDays(-3), "0001234567-26-000002", "form4-bad.xml"));

        var handler = CreateCombinedHandler(submissionsJson, filingDocuments: new()
        {
            ["form4-good.xml"] = (HttpStatusCode.OK, Filing1Xml),
            ["form4-bad.xml"] = (HttpStatusCode.OK, MalformedXml)
        });
        var feeder = CreateFeeder(handler, dbContext);

        // Must not throw - the malformed filing is isolated, the good filing still processed.
        var result = await feeder.FetchInsiderPurchasesAsync("AAPL", CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Single(result, o => o.Verified && o.TransactionDate == new DateOnly(2026, 7, 10));
        Assert.Single(result, o => !o.Verified);

        Assert.Equal(2, await dbContext.RawInsiderObservations.CountAsync());
    }

    [Fact]
    public async Task FetchInsiderPurchasesAsync_NoForm4FilingsInWindow_PersistsScannedCleanMarkerRow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        // A well-formed, successful submissions response whose only filing is outside the
        // 60-day window and isn't even a Form 4 - "verified, zero transactions," not a
        // failure and not silence: a real Verified=true marker row, distinguishable from a
        // ticker that was never scanned at all (see InsiderLookupServiceTests for why).
        var submissionsJson = BuildSubmissionsFixture(
            ("10-K", DateTime.UtcNow.AddDays(-90), "0001234567-26-000099", "10k.htm"));

        var handler = CreateCombinedHandler(submissionsJson);
        var feeder = CreateFeeder(handler, dbContext);

        var result = await feeder.FetchInsiderPurchasesAsync("AAPL", CancellationToken.None);

        var observation = Assert.Single(result);
        Assert.Equal("AAPL", observation.Ticker);
        Assert.Equal(SourceType.SecEdgarForm4, observation.Source);
        Assert.True(observation.Verified);
        Assert.Equal(0m, observation.DollarValue);
        Assert.Null(observation.InsiderName);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), observation.TransactionDate);

        Assert.Single(await dbContext.RawInsiderObservations.ToListAsync());
    }

    [Fact]
    public async Task FetchInsiderPurchasesAsync_FilingsExistButContainNoPurchaseTransactions_PersistsScannedCleanMarkerRow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        // A real Form 4 filing IS found in the window and parses successfully - it just
        // contains no "P" transaction, only an "S". Distinct code path from "zero filings at
        // all" above (this one actually fetches and parses a filing), same expected outcome.
        var submissionsJson = BuildSubmissionsFixture(
            ("4", DateTime.UtcNow.AddDays(-10), "0001234567-26-000001", "form4-sale-only.xml"));

        var handler = CreateCombinedHandler(submissionsJson, filingDocuments: new()
        {
            ["form4-sale-only.xml"] = (HttpStatusCode.OK, FilingOnlySaleXml)
        });
        var feeder = CreateFeeder(handler, dbContext);

        var result = await feeder.FetchInsiderPurchasesAsync("AAPL", CancellationToken.None);

        var observation = Assert.Single(result);
        Assert.True(observation.Verified);
        Assert.Equal(0m, observation.DollarValue);

        Assert.Single(await dbContext.RawInsiderObservations.ToListAsync());
    }

    [Fact]
    public void ParseForm4FilingsInWindow_FiltersToFormFourWithinWindowOnly()
    {
        var now = DateTime.UtcNow;
        var submissionsJson = BuildSubmissionsFixture(
            ("4", now.AddDays(-10), "0001234567-26-000001", "form4-1.xml"),
            ("10-Q", now.AddDays(-5), "0001234567-26-000002", "10q.htm"),
            ("4", now.AddDays(-90), "0001234567-26-000003", "too-old.xml"));

        var windowEnd = DateOnly.FromDateTime(now);
        var windowStart = windowEnd.AddDays(-60);

        var filings = EdgarForm4Feeder.ParseForm4FilingsInWindow(submissionsJson, windowStart, windowEnd);

        var filing = Assert.Single(filings);
        Assert.Equal("0001234567-26-000001", filing.AccessionNumber);
        Assert.Equal("form4-1.xml", filing.PrimaryDocument);
    }

    [Fact]
    public void ParseForm4FilingsInWindow_ResponseMissingFilingsRecentShape_Throws()
    {
        Assert.ThrowsAny<Exception>(() =>
            EdgarForm4Feeder.ParseForm4FilingsInWindow("""{"cik": "320193"}""", DateOnly.MinValue, DateOnly.MaxValue));
    }

    [Fact]
    public void ParsePurchaseTransactions_MalformedXml_Throws()
    {
        Assert.ThrowsAny<Exception>(() => EdgarForm4Feeder.ParsePurchaseTransactions(MalformedXml));
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
