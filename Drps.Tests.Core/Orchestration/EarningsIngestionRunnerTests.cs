using System.Net;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Ingestion.Orchestration;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Orchestration;

public class EarningsIngestionRunnerTests
{
    private static readonly DateOnly RunDate = new(2026, 8, 3);
    private const string GuardWorkerName = "Drps.Earnings";

    // Well-documented Finnhub /calendar/earnings response shape - same fixture convention as
    // FinnhubEarningsFeederTests.cs's own BuildFixture. Date is relative to real UtcNow at
    // test-run time, not a hardcoded literal - FinnhubEarningsFeeder now filters entries to
    // date >= today (CLAUDE.md's "Earnings Verification Tri-State Fix," 2026-08-07), so a
    // fixed past-dated literal would silently start failing once real time passed it by.
    private static string SingleEntryFixture =>
        $$"""{"earningsCalendar": [{"date": "{{DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10):yyyy-MM-dd}}", "symbol": "AAPL"}]}""";

    // FinnhubEarningsFeeder has no interface (ISectorFeeder-style abstraction doesn't exist for
    // it), so this Runner's dependency is the real, concrete class - same "real object, fake
    // transport" pattern FinnhubEarningsFeederTests.cs itself already uses to test the feeder in
    // isolation, applied here one layer up. Shares the SAME DbContext instance the runner itself
    // uses, so both sides' writes (RawEarningsObservations from the feeder, WorkerRunRecords from
    // the runner's guard) land in one place this test can assert against together.
    private static FinnhubEarningsFeeder CreateFeeder(
        DrpsDbContext dbContext, Func<HttpRequestMessage, HttpResponseMessage> respond, string apiKey = "test-key")
    {
        var handler = new FakeHttpMessageHandler(respond);
        var httpClientFactory = new FakeHttpClientFactory(new HttpClient(handler));
        var options = Options.Create(new FinnhubOptions { ApiKey = apiKey });
        return new FinnhubEarningsFeeder(httpClientFactory, options, dbContext, NullLogger<FinnhubEarningsFeeder>.Instance);
    }

    [Fact]
    public async Task RunAsync_HappyPath_PersistsObservationForEveryTickerAndRecordsGuardSuccessOnce()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var callCount = 0;
        var feeder = CreateFeeder(dbContext, _ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SingleEntryFixture) };
        });
        var runner = new EarningsIngestionRunner(feeder, dbContext, NullLogger<EarningsIngestionRunner>.Instance);

        await runner.RunAsync(new[] { "AAPL", "MSFT" }, RunDate, CancellationToken.None);

        Assert.Equal(2, callCount);

        var rows = await dbContext.RawEarningsObservations.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Ticker == "AAPL" && r.Source == SourceType.Finnhub && r.FetchOutcome == EarningsFetchOutcome.UpcomingEarningsFound);
        Assert.Contains(rows, r => r.Ticker == "MSFT" && r.Source == SourceType.Finnhub && r.FetchOutcome == EarningsFetchOutcome.UpcomingEarningsFound);

        // Recorded exactly once for the whole cycle, not once per ticker - the cycle-level
        // guard granularity this Runner is deliberately built around (see its own doc comment).
        var guardRows = await dbContext.WorkerRunRecords.Where(r => r.WorkerName == GuardWorkerName).ToListAsync();
        var guardRow = Assert.Single(guardRows);
        Assert.Equal(RunDate, guardRow.RunDate);

        var hasRun = await WorkerRunGuard.HasRunTodayAsync(dbContext, GuardWorkerName, RunDate, CancellationToken.None);
        Assert.True(hasRun);
    }

    [Fact]
    public async Task RunAsync_AlreadyRanTodayPerGuard_SkipsFetchEntirelyAndWritesNoObservations()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await WorkerRunGuard.RecordSuccessfulRunAsync(
            dbContext, GuardWorkerName, RunDate, new DateTime(2026, 8, 3, 3, 5, 0), CancellationToken.None);

        var callCount = 0;
        var feeder = CreateFeeder(dbContext, _ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SingleEntryFixture) };
        });
        var runner = new EarningsIngestionRunner(feeder, dbContext, NullLogger<EarningsIngestionRunner>.Instance);

        await runner.RunAsync(new[] { "AAPL", "MSFT" }, RunDate, CancellationToken.None);

        // The guard check short-circuits before the feeder is ever touched, for any ticker.
        Assert.Equal(0, callCount);
        Assert.Empty(await dbContext.RawEarningsObservations.ToListAsync());

        // Still exactly the one pre-seeded guard row - the skip path doesn't write a duplicate.
        var guardRows = await dbContext.WorkerRunRecords.Where(r => r.WorkerName == GuardWorkerName).ToListAsync();
        Assert.Single(guardRows);
    }

    [Fact]
    public async Task RunAsync_GuardRecordExistsForADifferentDate_StillFetchesForNewDate()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await WorkerRunGuard.RecordSuccessfulRunAsync(
            dbContext, GuardWorkerName, RunDate.AddDays(-1), new DateTime(2026, 8, 2, 3, 5, 0), CancellationToken.None);

        var feeder = CreateFeeder(dbContext, _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SingleEntryFixture) });
        var runner = new EarningsIngestionRunner(feeder, dbContext, NullLogger<EarningsIngestionRunner>.Instance);

        await runner.RunAsync(new[] { "AAPL" }, RunDate, CancellationToken.None);

        // The guard is scoped per (WorkerName, RunDate) - a prior date's recorded run must not
        // suppress today's fetch.
        Assert.Single(await dbContext.RawEarningsObservations.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_MissingApiKey_AbortsWithoutPersistingAnyObservationOrRecordingGuardSuccess()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        // FinnhubEarningsFeeder throws ConfigurationMissingException before any HTTP call is
        // ever attempted (its own doc comment) - a genuine call here means the abort didn't
        // actually happen before touching the network.
        var feeder = CreateFeeder(
            dbContext,
            _ => throw new InvalidOperationException("HTTP call should never have been attempted"),
            apiKey: "");
        var runner = new EarningsIngestionRunner(feeder, dbContext, NullLogger<EarningsIngestionRunner>.Instance);

        var exception = await Record.ExceptionAsync(() =>
            runner.RunAsync(new[] { "AAPL", "MSFT", "TSLA" }, RunDate, CancellationToken.None));

        // The runner itself never lets ConfigurationMissingException escape - it's caught and
        // treated as a whole-run abort, not rethrown to the caller.
        Assert.Null(exception);
        Assert.Empty(await dbContext.RawEarningsObservations.ToListAsync());

        // The actual, code-verifiable proof the loop was abandoned via the early `return` rather
        // than continuing (every remaining ticker would throw the identical
        // ConfigurationMissingException, since the missing key is feeder-instance-wide, not
        // per-ticker - so an incorrect "treat as a per-ticker failure and keep looping"
        // implementation would still reach, and call, RecordSuccessfulRunAsync at the end of the
        // loop, same as the generic-Exception path below does). No WorkerRunRecord row at all is
        // what distinguishes the real early-abort behavior from that hypothetical alternative.
        Assert.Empty(await dbContext.WorkerRunRecords.Where(r => r.WorkerName == GuardWorkerName).ToListAsync());
        var hasRun = await WorkerRunGuard.HasRunTodayAsync(dbContext, GuardWorkerName, RunDate, CancellationToken.None);
        Assert.False(hasRun);
    }

    [Fact]
    public async Task RunAsync_OneTickerThrowsUnexpectedException_ContinuesToRemainingTickersAndStillRecordsGuardSuccess()
    {
        // FinnhubEarningsFeeder itself never throws a generic exception for bad/malformed HTTP
        // responses - its own internal try/catch (confirmed by reading the real implementation)
        // collapses every fetch/parse failure into an explicit FetchOutcome=Unknown row instead. The
        // one call inside FetchNextEarningsAsync NOT covered by that try/catch is the DB
        // persistence step itself (dbContext.SaveChangesAsync), so a genuinely unexpected
        // exception reaching this Runner's per-ticker catch block is modeled here as a real DB
        // save failure for exactly one ticker - the same category of "something genuinely
        // unexpected happened" this Runner's own catch(Exception) comment describes.
        var options = new DbContextOptionsBuilder<DrpsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        // Throws on the 2nd SaveChangesAsync call overall - AAPL's own persist (call 1) succeeds,
        // MSFT's persist (call 2) fails, TSLA's persist (call 3) succeeds, and the guard's own
        // RecordSuccessfulRunAsync call (call 4) succeeds. ChangeTracker.Clear() on the throwing
        // call discards MSFT's pending, uncommitted Add() so it isn't silently swept into a
        // later ticker's successful save.
        using var dbContext = new ThrowOnNthSaveDbContext(options, throwOnCallNumber: 2);

        var feeder = CreateFeeder(dbContext, _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SingleEntryFixture) });
        var runner = new EarningsIngestionRunner(feeder, dbContext, NullLogger<EarningsIngestionRunner>.Instance);

        var exception = await Record.ExceptionAsync(() =>
            runner.RunAsync(new[] { "AAPL", "MSFT", "TSLA" }, RunDate, CancellationToken.None));

        Assert.Null(exception);

        // MSFT's own observation never landed (its save failed and was discarded); AAPL and
        // TSLA - the tickers on either side of it - both did, proving the loop continued past
        // MSFT's failure rather than aborting.
        var tickers = (await dbContext.RawEarningsObservations.Select(r => r.Ticker).ToListAsync()).OrderBy(t => t).ToList();
        Assert.Equal(new[] { "AAPL", "TSLA" }, tickers);

        // The cycle still reached its end and recorded success, unlike the ConfigurationMissingException
        // case above - a per-ticker failure is isolated, not treated as a reason to abort the
        // whole run or withhold the guard row.
        var hasRun = await WorkerRunGuard.HasRunTodayAsync(dbContext, GuardWorkerName, RunDate, CancellationToken.None);
        Assert.True(hasRun);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    // Test-only DbContext subclass that fails exactly one SaveChangesAsync call (identified by
    // its 1-based position across the whole test, not per-entity-type) to simulate a genuine,
    // unexpected persistence failure for a single ticker mid-sweep - see the test above for why
    // this is the only reachable way to make the real FinnhubEarningsFeeder throw a non-
    // ConfigurationMissingException exception, given it deliberately swallows every other
    // failure mode itself.
    private sealed class ThrowOnNthSaveDbContext : DrpsDbContext
    {
        private readonly int _throwOnCallNumber;
        private int _saveCallCount;

        public ThrowOnNthSaveDbContext(DbContextOptions<DrpsDbContext> options, int throwOnCallNumber) : base(options)
        {
            _throwOnCallNumber = throwOnCallNumber;
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _saveCallCount++;
            if (_saveCallCount == _throwOnCallNumber)
            {
                // Discards the pending, uncommitted Add() for this call so it can't be silently
                // swept into a later, successful SaveChangesAsync call on this same shared
                // context - simulates a real save failure leaving nothing persisted.
                ChangeTracker.Clear();
                throw new InvalidOperationException("Simulated unexpected DB failure for this ticker's save");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
