using System.Net;
using Drps.Ingestion.Orchestration;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Orchestration;

public class UniverseIngestionRunnerTests
{
    private static readonly DateOnly SnapshotDate = new(2026, 7, 22);

    // Real nasdaqlisted.txt shape: header row, one valid ticker, one too-long ticker (filtered
    // by TickerSymbolHelper.IsValidSymbol's length check), one warrant-suffixed ticker
    // (filtered by the suffix check), then the "File Creation Time" footer.
    private const string NasdaqFixture =
        "Symbol|Security Name|Market Category|Test Issue|Financial Status|Round Lot Size|ETF|NextShares\n" +
        "AAPL|Apple Inc|Q|N|N|100|N|N\n" +
        "TOOLONGX|Some Test Issue|Q|N|N|100|N|N\n" +
        "ABCW|ABC Corp Warrant|Q|N|N|100|N|N\n" +
        "File Creation Time: 0722202608:00\n";

    // Real otherlisted.txt shape - header starts "ACT Symbol", not "Symbol", the exact
    // incidental-filtering case documented on UniverseIngestionRunner.ParseTickers.
    private const string OtherFixture =
        "ACT Symbol|Security Name|Exchange|CQS Symbol|ETF|Round Lot Size|Test Issue|NASDAQ Symbol\n" +
        "IBM|International Business Machines|N|IBM|N|100|N|IBM\n" +
        "File Creation Time: 0722202608:00\n";

    private static HttpResponseMessage RespondByUrl(
        HttpRequestMessage request, string nasdaqBody, HttpStatusCode nasdaqStatus, string otherBody, HttpStatusCode otherStatus)
    {
        var url = request.RequestUri!.ToString();
        return url.Contains("nasdaqlisted", StringComparison.OrdinalIgnoreCase)
            ? new HttpResponseMessage(nasdaqStatus) { Content = new StringContent(nasdaqBody) }
            : new HttpResponseMessage(otherStatus) { Content = new StringContent(otherBody) };
    }

    [Fact]
    public async Task RunAsync_BothSourcesSucceed_PersistsRowsTaggedWithExchangeAndFiltersInvalidSymbols()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var handler = new FakeHttpMessageHandler(req =>
            RespondByUrl(req, NasdaqFixture, HttpStatusCode.OK, OtherFixture, HttpStatusCode.OK));
        var runner = new UniverseIngestionRunner(
            new FakeHttpClientFactory(new HttpClient(handler)), dbContext, NullLogger<UniverseIngestionRunner>.Instance);

        await runner.RunAsync(SnapshotDate, CancellationToken.None);

        var rows = await dbContext.UniverseSnapshots.ToListAsync();

        // Only AAPL (NASDAQ) and IBM (NYSE) survive - TOOLONGX, ABCW, both header rows, and
        // the footer are all filtered out or skipped.
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Ticker == "AAPL" && r.Exchange == "NASDAQ" && r.SnapshotDate == SnapshotDate);
        Assert.Contains(rows, r => r.Ticker == "IBM" && r.Exchange == "NYSE" && r.SnapshotDate == SnapshotDate);

        var coverage = await dbContext.UniverseSnapshotCoverages.SingleAsync(c => c.SnapshotDate == SnapshotDate);
        Assert.Equal(UniverseSourceCoverage.All, coverage.SourcesSucceeded);
    }

    [Fact]
    public async Task RunAsync_NasdaqFailsOtherSucceeds_PersistsPartialSnapshotFromSurvivingSource()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var handler = new FakeHttpMessageHandler(req =>
            RespondByUrl(req, string.Empty, HttpStatusCode.ServiceUnavailable, OtherFixture, HttpStatusCode.OK));
        var runner = new UniverseIngestionRunner(
            new FakeHttpClientFactory(new HttpClient(handler)), dbContext, NullLogger<UniverseIngestionRunner>.Instance);

        await runner.RunAsync(SnapshotDate, CancellationToken.None);

        var rows = await dbContext.UniverseSnapshots.ToListAsync();

        // A single source's failure does not block the other - the surviving source's
        // tickers are still persisted for this date.
        var row = Assert.Single(rows);
        Assert.Equal("IBM", row.Ticker);
        Assert.Equal("NYSE", row.Exchange);

        // The partial-day gap this task exists to close: the coverage record makes it explicit
        // that NASDAQ's tickers are silently absent, rather than this day looking identical to
        // a normal, fully-covered one.
        var coverage = await dbContext.UniverseSnapshotCoverages.SingleAsync(c => c.SnapshotDate == SnapshotDate);
        Assert.Equal(UniverseSourceCoverage.NyseAmex, coverage.SourcesSucceeded);
    }

    [Fact]
    public async Task RunAsync_OtherFailsNasdaqSucceeds_PersistsPartialSnapshotFromSurvivingSource()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var handler = new FakeHttpMessageHandler(req =>
            RespondByUrl(req, NasdaqFixture, HttpStatusCode.OK, string.Empty, HttpStatusCode.ServiceUnavailable));
        var runner = new UniverseIngestionRunner(
            new FakeHttpClientFactory(new HttpClient(handler)), dbContext, NullLogger<UniverseIngestionRunner>.Instance);

        await runner.RunAsync(SnapshotDate, CancellationToken.None);

        var rows = await dbContext.UniverseSnapshots.ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal("AAPL", row.Ticker);
        Assert.Equal("NASDAQ", row.Exchange);

        var coverage = await dbContext.UniverseSnapshotCoverages.SingleAsync(c => c.SnapshotDate == SnapshotDate);
        Assert.Equal(UniverseSourceCoverage.Nasdaq, coverage.SourcesSucceeded);
    }

    [Fact]
    public async Task RunAsync_BothSourcesFail_LeavesSnapshotAbsentRatherThanPersistingEmptyRow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var handler = new FakeHttpMessageHandler(req =>
            RespondByUrl(req, string.Empty, HttpStatusCode.ServiceUnavailable, string.Empty, HttpStatusCode.ServiceUnavailable));
        var runner = new UniverseIngestionRunner(
            new FakeHttpClientFactory(new HttpClient(handler)), dbContext, NullLogger<UniverseIngestionRunner>.Instance);

        var exception = await Record.ExceptionAsync(() => runner.RunAsync(SnapshotDate, CancellationToken.None));

        // Fail-closed: no exception (a total-fetch-failure is a real, expected outcome, not a
        // bug), but also no ticker row of any kind written for this date - the deliberate
        // divergence from CapitalFill's fail-open "stale cache preserved" behavior.
        Assert.Null(exception);
        Assert.Empty(await dbContext.UniverseSnapshots.ToListAsync());

        // The coverage row IS still written even though no ticker rows exist - this date reads
        // as "attempted, both sources failed," not indistinguishable from "never attempted."
        var coverage = await dbContext.UniverseSnapshotCoverages.SingleAsync(c => c.SnapshotDate == SnapshotDate);
        Assert.Equal(UniverseSourceCoverage.None, coverage.SourcesSucceeded);
    }

    [Fact]
    public async Task RunAsync_AlreadyRanTodayPerGuard_SkipsFetchEntirely()
    {
        // Standardized onto WorkerRunGuard (CLAUDE.md's guard-standardization task, 2026-07-24) -
        // "already ran today" is now answered by a WorkerRunRecord row, not by whether a
        // UniverseSnapshot row happens to already exist for the date.
        using var dbContext = InMemoryDbContextFactory.Create();
        await WorkerRunGuard.RecordSuccessfulRunAsync(
            dbContext, "Drps.UniverseSnapshot", SnapshotDate, new DateTime(2026, 7, 22, 3, 0, 0), CancellationToken.None);

        var callCount = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            callCount++;
            return RespondByUrl(req, NasdaqFixture, HttpStatusCode.OK, OtherFixture, HttpStatusCode.OK);
        });
        var runner = new UniverseIngestionRunner(
            new FakeHttpClientFactory(new HttpClient(handler)), dbContext, NullLogger<UniverseIngestionRunner>.Instance);

        await runner.RunAsync(SnapshotDate, CancellationToken.None);

        // No HTTP call at all - the guard check short-circuits before either fetch is attempted.
        Assert.Equal(0, callCount);

        // No snapshot or coverage row is written - the guard skips the whole run.
        Assert.Empty(await dbContext.UniverseSnapshots.ToListAsync());
        Assert.Empty(await dbContext.UniverseSnapshotCoverages.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_GuardRecordExistsForADifferentDate_StillFetchesForNewDate()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await WorkerRunGuard.RecordSuccessfulRunAsync(
            dbContext, "Drps.UniverseSnapshot", SnapshotDate.AddDays(-1), new DateTime(2026, 7, 21, 3, 0, 0), CancellationToken.None);

        var handler = new FakeHttpMessageHandler(req =>
            RespondByUrl(req, NasdaqFixture, HttpStatusCode.OK, OtherFixture, HttpStatusCode.OK));
        var runner = new UniverseIngestionRunner(
            new FakeHttpClientFactory(new HttpClient(handler)), dbContext, NullLogger<UniverseIngestionRunner>.Instance);

        await runner.RunAsync(SnapshotDate, CancellationToken.None);

        // The guard is scoped per (WorkerName, RunDate) - a prior date's recorded run must not
        // suppress today's fetch.
        var todayRows = await dbContext.UniverseSnapshots.Where(s => s.SnapshotDate == SnapshotDate).ToListAsync();
        Assert.Equal(2, todayRows.Count);
    }

    [Fact]
    public async Task RunAsync_BothSourcesSucceed_RecordsSuccessInGuard()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var handler = new FakeHttpMessageHandler(req =>
            RespondByUrl(req, NasdaqFixture, HttpStatusCode.OK, OtherFixture, HttpStatusCode.OK));
        var runner = new UniverseIngestionRunner(
            new FakeHttpClientFactory(new HttpClient(handler)), dbContext, NullLogger<UniverseIngestionRunner>.Instance);

        await runner.RunAsync(SnapshotDate, CancellationToken.None);

        var hasRun = await WorkerRunGuard.HasRunTodayAsync(dbContext, "Drps.UniverseSnapshot", SnapshotDate, CancellationToken.None);
        Assert.True(hasRun);
    }

    [Fact]
    public async Task RunAsync_BothSourcesFail_StillRecordsSuccessInGuard()
    {
        // A total fetch failure is still a completed cycle (the fetch was genuinely attempted,
        // and the outcome is recorded via the coverage row) - same "ran to completion regardless
        // of per-item outcome" interpretation used elsewhere in this codebase. This is what
        // stops a same-day re-trigger from re-hitting NasdaqTrader again for a day already known
        // to have failed.
        using var dbContext = InMemoryDbContextFactory.Create();
        var handler = new FakeHttpMessageHandler(req =>
            RespondByUrl(req, string.Empty, HttpStatusCode.ServiceUnavailable, string.Empty, HttpStatusCode.ServiceUnavailable));
        var runner = new UniverseIngestionRunner(
            new FakeHttpClientFactory(new HttpClient(handler)), dbContext, NullLogger<UniverseIngestionRunner>.Instance);

        await runner.RunAsync(SnapshotDate, CancellationToken.None);

        var hasRun = await WorkerRunGuard.HasRunTodayAsync(dbContext, "Drps.UniverseSnapshot", SnapshotDate, CancellationToken.None);
        Assert.True(hasRun);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
