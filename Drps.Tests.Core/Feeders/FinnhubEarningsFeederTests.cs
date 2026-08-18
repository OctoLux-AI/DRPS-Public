using System.Net;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Ingestion.Persistence;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Feeders;

public class FinnhubEarningsFeederTests
{
    // Fixtures are built from dates relative to real UtcNow at test-run time, not hardcoded
    // literals - FetchNextEarningsAsync computes `start = DateOnly.FromDateTime(DateTime.UtcNow)`
    // internally and now filters entries to `date >= start` (CLAUDE.md's "Earnings
    // Verification Tri-State Fix," 2026-08-07), so a fixture meant to represent an "upcoming"
    // date must actually be upcoming whenever the test happens to run, not just on the day
    // this file was written.
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string BuildFixture(params DateOnly[] dates)
    {
        var entries = string.Join(",", dates.Select(d =>
            $$"""{"date": "{{d:yyyy-MM-dd}}", "epsEstimate": 1.42, "hour": "amc", "quarter": 3, "symbol": "AAPL", "year": 2026}"""));
        return $$"""{"earningsCalendar": [{{entries}}]}""";
    }

    private static FinnhubEarningsFeeder CreateFeeder(
        FakeHttpMessageHandler handler, out DrpsDbContext dbContext, FinnhubOptions? options = null)
    {
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var resolvedOptions = Options.Create(options ?? new FinnhubOptions { ApiKey = "test-key" });
        dbContext = InMemoryDbContextFactory.Create();
        return new FinnhubEarningsFeeder(httpClientFactory, resolvedOptions, dbContext, NullLogger<FinnhubEarningsFeeder>.Instance);
    }

    [Fact]
    public async Task FetchNextEarningsAsync_SuccessfulResponse_PersistsUpcomingEarningsFoundRowAndAuthenticatesViaHeader()
    {
        var earningsDate = Today.AddDays(24);
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BuildFixture(earningsDate)) });
        var feeder = CreateFeeder(handler, out var dbContext, new FinnhubOptions { ApiKey = "real-token" });

        var result = await feeder.FetchNextEarningsAsync("AAPL", CancellationToken.None);

        Assert.Equal("AAPL", result.Ticker);
        Assert.Equal(SourceType.Finnhub, result.Source);
        Assert.Equal(earningsDate, result.NextEarningsDate);
        Assert.Equal(EarningsFetchOutcome.UpcomingEarningsFound, result.FetchOutcome);

        var persisted = Assert.Single(dbContext.RawEarningsObservations);
        Assert.Equal(result.Id, persisted.Id);
        Assert.Equal(earningsDate, persisted.NextEarningsDate);
        Assert.Equal(EarningsFetchOutcome.UpcomingEarningsFound, persisted.FetchOutcome);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("real-token", handler.LastRequest!.Headers.GetValues("X-Finnhub-Token").Single());
    }

    [Fact]
    public async Task FetchNextEarningsAsync_MultipleEntriesOutOfOrder_PicksEarliestDate()
    {
        var laterDate = Today.AddDays(29);
        var earlierDate = Today.AddDays(24);
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BuildFixture(laterDate, earlierDate)) });
        var feeder = CreateFeeder(handler, out _);

        var result = await feeder.FetchNextEarningsAsync("AAPL", CancellationToken.None);

        Assert.Equal(earlierDate, result.NextEarningsDate);
        Assert.Equal(EarningsFetchOutcome.UpcomingEarningsFound, result.FetchOutcome);
    }

    [Fact]
    public async Task FetchNextEarningsAsync_NonRetryableClientError_PersistsUnknownOutcomeRowWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"Invalid API key"}""")
            });
        var feeder = CreateFeeder(handler, out var dbContext);

        var result = await feeder.FetchNextEarningsAsync("AAPL", CancellationToken.None);

        Assert.Null(result.NextEarningsDate);
        Assert.Equal(EarningsFetchOutcome.Unknown, result.FetchOutcome);

        var persisted = Assert.Single(dbContext.RawEarningsObservations);
        Assert.Equal("AAPL", persisted.Ticker);
        Assert.Null(persisted.NextEarningsDate);
        Assert.Equal(EarningsFetchOutcome.Unknown, persisted.FetchOutcome);
    }

    [Fact]
    public async Task FetchNextEarningsAsync_UnparseableJsonBody_PersistsUnknownOutcomeRowWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{not valid json") });
        var feeder = CreateFeeder(handler, out var dbContext);

        var result = await feeder.FetchNextEarningsAsync("AAPL", CancellationToken.None);

        Assert.Null(result.NextEarningsDate);
        Assert.Equal(EarningsFetchOutcome.Unknown, result.FetchOutcome);
        Assert.Single(dbContext.RawEarningsObservations);
    }

    [Fact]
    public async Task FetchNextEarningsAsync_ResponseMissingEarningsCalendarProperty_PersistsUnknownOutcomeRowWithoutThrowing()
    {
        const string noCalendarFixture = """{"symbol": "AAPL"}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(noCalendarFixture) });
        var feeder = CreateFeeder(handler, out var dbContext);

        var result = await feeder.FetchNextEarningsAsync("AAPL", CancellationToken.None);

        Assert.Null(result.NextEarningsDate);
        Assert.Equal(EarningsFetchOutcome.Unknown, result.FetchOutcome);
        Assert.Single(dbContext.RawEarningsObservations);
    }

    [Fact]
    public async Task FetchNextEarningsAsync_EmptyEarningsCalendarArray_PersistsNoUpcomingEarningsInWindowRowWithoutThrowing()
    {
        const string emptyCalendarFixture = """{"earningsCalendar": []}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(emptyCalendarFixture) });
        var feeder = CreateFeeder(handler, out var dbContext);

        var result = await feeder.FetchNextEarningsAsync("AAPL", CancellationToken.None);

        Assert.Null(result.NextEarningsDate);
        Assert.Equal(EarningsFetchOutcome.NoUpcomingEarningsInWindow, result.FetchOutcome);
        Assert.Single(dbContext.RawEarningsObservations);
    }

    [Fact]
    public async Task FetchNextEarningsAsync_EntryMissingDateField_PersistsNoUpcomingEarningsInWindowRowWithoutThrowing()
    {
        // A well-formed earningsCalendar array with one individually-malformed entry is NOT
        // the same failure class as a wrong top-level shape - the array itself is real, it
        // just yields nothing after filtering, same as a genuinely empty array. This is
        // deliberately NOT Unknown (CLAUDE.md's "Earnings Verification Tri-State Fix,"
        // 2026-08-07 - see ExtractEarliestDate's own doc comment for the full reasoning).
        const string noDateFieldFixture = """{"earningsCalendar": [{"symbol": "AAPL", "hour": "amc"}]}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(noDateFieldFixture) });
        var feeder = CreateFeeder(handler, out var dbContext);

        var result = await feeder.FetchNextEarningsAsync("AAPL", CancellationToken.None);

        Assert.Null(result.NextEarningsDate);
        Assert.Equal(EarningsFetchOutcome.NoUpcomingEarningsInWindow, result.FetchOutcome);
        Assert.Single(dbContext.RawEarningsObservations);
    }

    [Fact]
    public async Task FetchNextEarningsAsync_OnlyPastDateEntry_YieldsNoUpcomingEarningsInWindowNotThePastDate()
    {
        // The actual regression test for the 2026-08-07 audit's finding: without the
        // `date >= start` filter, this past-only response would have had its already-elapsed
        // date treated as "the next earnings date." Confirms it does not.
        var pastDate = Today.AddDays(-3);
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BuildFixture(pastDate)) });
        var feeder = CreateFeeder(handler, out var dbContext);

        var result = await feeder.FetchNextEarningsAsync("AAPL", CancellationToken.None);

        Assert.Null(result.NextEarningsDate);
        Assert.NotEqual(pastDate, result.NextEarningsDate);
        Assert.Equal(EarningsFetchOutcome.NoUpcomingEarningsInWindow, result.FetchOutcome);
        Assert.Single(dbContext.RawEarningsObservations);
    }

    [Fact]
    public async Task FetchNextEarningsAsync_MixOfPastAndFutureDates_PicksEarliestFutureDateIgnoringPastOne()
    {
        var pastDate = Today.AddDays(-5);
        var futureDate = Today.AddDays(10);
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BuildFixture(pastDate, futureDate)) });
        var feeder = CreateFeeder(handler, out var dbContext);

        var result = await feeder.FetchNextEarningsAsync("AAPL", CancellationToken.None);

        Assert.Equal(futureDate, result.NextEarningsDate);
        Assert.Equal(EarningsFetchOutcome.UpcomingEarningsFound, result.FetchOutcome);
    }

    [Fact]
    public async Task FetchNextEarningsAsync_EntryDatedExactlyToday_IsIncludedAsUpcoming()
    {
        // The `>= start` boundary is inclusive of today itself, deliberately - Finnhub's
        // dates carry no time-of-day certainty, so a same-day entry may genuinely not have
        // reported yet (e.g. an "amc" - after market close - report).
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(BuildFixture(Today)) });
        var feeder = CreateFeeder(handler, out var dbContext);

        var result = await feeder.FetchNextEarningsAsync("AAPL", CancellationToken.None);

        Assert.Equal(Today, result.NextEarningsDate);
        Assert.Equal(EarningsFetchOutcome.UpcomingEarningsFound, result.FetchOutcome);
    }

    [Fact]
    public async Task FetchNextEarningsAsync_MissingApiKey_ThrowsConfigurationMissingExceptionAndDoesNotPersist()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP call should never have been attempted"));
        var feeder = CreateFeeder(handler, out var dbContext, new FinnhubOptions { ApiKey = "" });

        var exception = await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            feeder.FetchNextEarningsAsync("AAPL", CancellationToken.None));

        Assert.Equal("Finnhub:ApiKey", exception.ConfigKey);
        Assert.Null(handler.LastRequest);
        Assert.Empty(dbContext.RawEarningsObservations);
    }

    // This test takes several real seconds: the retry policy's exponential backoff (2s, 4s,
    // 8s between attempts) actually elapses, same caveat as the existing Alpaca/Tiingo/
    // sector/ex-dividend tests.
    [Fact]
    public async Task FetchNextEarningsAsync_PersistentTransientFailure_RetriesThreeTimesThenPersistsUnknownOutcomeRow()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });
        var feeder = CreateFeeder(handler, out var dbContext);

        var result = await feeder.FetchNextEarningsAsync("AAPL", CancellationToken.None);

        Assert.Equal(4, callCount);
        Assert.Null(result.NextEarningsDate);
        Assert.Equal(EarningsFetchOutcome.Unknown, result.FetchOutcome);
        Assert.Single(dbContext.RawEarningsObservations);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
