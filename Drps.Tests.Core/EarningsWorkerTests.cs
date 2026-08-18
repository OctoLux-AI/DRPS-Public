using System.Net;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Ingestion.Orchestration;
using Drps.Ingestion.Persistence;
using Drps.Shared.Scheduling;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests;

public class EarningsWorkerTests
{
    // Fixed instant just before 03:00 - NextRunTimeCalculator.GetNextRunTime resolves this to
    // "today's 03:25" (EarningsWorker's own RunTime = DailyRunTime + 25 minutes), a real but
    // tiny delay each cycle. Same fixed-clock pattern as every sibling Worker test in this
    // codebase (RegimeWorkerTests/WorkerTests' own JustBeforeThreeAm fixtures).
    private static readonly Func<DateTime> JustBeforeThreeAm = () => new DateTime(2026, 8, 8, 2, 59, 59, 950);

    private static string SingleEntryFixture(string symbol) =>
        $$"""{"earningsCalendar": [{"date": "{{DateOnly.FromDateTime(DateTime.UtcNow).AddDays(10):yyyy-MM-dd}}", "symbol": "{{symbol}}"}]}""";

    // Builds a real, minimal DI container wiring EarningsIngestionRunner and a real
    // FinnhubEarningsFeeder (fake HTTP transport only, matching EarningsIngestionRunnerTests'
    // own CreateFeeder convention) against a single shared in-memory DrpsDbContext, so the whole
    // Worker -> Runner -> Feeder -> DB chain can be exercised end-to-end without a real network
    // call or a real database.
    //
    // IDma5AlignedCandidateSource is registered AddScoped here (matching production's own
    // registration lifetime, Program.cs) rather than passed directly to EarningsWorker's
    // constructor - the DI-lifetime-violation fix (EarningsWorker now resolves it via
    // IServiceScopeFactory.CreateScope() at the point of use, same as EarningsIngestionRunner
    // already was) means the constructor no longer accepts it at all.
    private static (IServiceScopeFactory ScopeFactory, DrpsDbContext DbContext) BuildScopeFactory(
        IReadOnlyList<string> candidateTickers, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var dbContext = InMemoryDbContextFactory.Create();
        var services = new ServiceCollection();
        services.AddSingleton(dbContext);
        // EarningsIngestionRunner's own constructor needs ILogger<EarningsIngestionRunner> via
        // DI - a bare ServiceCollection with no AddLogging() call cannot resolve that, and
        // GetRequiredService would throw. Registered directly as a no-op logger rather than
        // pulling in a full logging pipeline this test has no use for.
        services.AddSingleton<ILogger<EarningsIngestionRunner>>(NullLogger<EarningsIngestionRunner>.Instance);
        services.AddScoped<EarningsIngestionRunner>();
        services.AddScoped(sp => new FinnhubEarningsFeeder(
            new FakeHttpClientFactory(new HttpClient(new FakeHttpMessageHandler(respond))),
            Options.Create(new FinnhubOptions { ApiKey = "test-key" }),
            sp.GetRequiredService<DrpsDbContext>(),
            NullLogger<FinnhubEarningsFeeder>.Instance));
        services.AddScoped<IDma5AlignedCandidateSource>(_ => new FakeDma5AlignedCandidateSource(candidateTickers));

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IServiceScopeFactory>(), dbContext);
    }

    [Fact]
    public async Task RunTime_Is25MinutesAfterTheSharedDailyDefault()
    {
        // CLAUDE.md's "FIX: Repoint EarningsWorker to Calculator's dynamic DMA-5-aligned
        // candidate list" (2026-08-08) - retimed from 03:05 to 03:25 (DailyRunTime + 25 minutes),
        // after Calculator's own 03:20 run so the candidate list this Worker reads reflects that
        // same night's real DMA-5 alignment state. Verified indirectly via the scheduled-loop's
        // own "next scheduled run at" log line rather than reflection against a private field,
        // matching this codebase's existing sibling Worker tests (RegimeWorkerTests etc.), which
        // assert scheduling behavior the same way.
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var logger = new CapturingLogger<EarningsWorker>();

        var worker = new EarningsWorker(logger, scopeFactory, JustBeforeThreeAm);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (logger.Messages.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        // Computed independently, not hardcoded as a literal date string - EarningsWorker's own
        // RunTime field is private, so this proves the 25-minute retime by recomputing the same
        // NextRunTimeCalculator formula the fix changed (DailyRunTime + 25 minutes). Asserted via
        // the logged "(in {Delay})" TimeSpan rather than the "next scheduled run at {NextRunAt}"
        // DateTime - TimeSpan's default ToString() is culture-invariant, unlike DateTime's,
        // which would otherwise make this assertion fragile against whatever culture the test
        // host happens to run under.
        var expectedNextRunAt = NextRunTimeCalculator.GetNextRunTime(
            JustBeforeThreeAm(), NextRunTimeCalculator.DailyRunTime + TimeSpan.FromMinutes(25));
        var expectedDelay = expectedNextRunAt - JustBeforeThreeAm();
        var message = Assert.Single(logger.Messages, m => m.Contains("next scheduled run at"));
        Assert.Contains(expectedDelay.ToString(), message);
    }

    [Fact]
    public async Task ExecuteAsync_NamedRunNow_UsesCandidateSourceTickersNotAStaticWatchlist()
    {
        // The actual fix this task verifies: EarningsWorker no longer has any dependency on
        // WatchlistOptions at all - the exact ticker set persisted below can only have come from
        // IDma5AlignedCandidateSource, since nothing else in this test wires a ticker list in.
        var (scopeFactory, dbContext) = BuildScopeFactory(new[] { "AAPL", "MSFT" }, _ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SingleEntryFixture("AAPL")) });

        var logger = new CapturingLogger<EarningsWorker>();
        var worker = new EarningsWorker(
            logger, scopeFactory, JustBeforeThreeAm, new[] { "--run-now=Drps.Earnings" });

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        var persistedTickers = (await dbContext.RawEarningsObservations.Select(r => r.Ticker).ToListAsync())
            .OrderBy(t => t)
            .ToList();
        Assert.Equal(new[] { "AAPL", "MSFT" }, persistedTickers);

        Assert.Contains(logger.Messages,
            m => m.Contains("named on-demand run for Drps.Earnings complete") && m.Contains("execution ends here"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("stopping host"));
    }

    [Fact]
    public async Task ExecuteAsync_CandidateSourceReturnsEmptyList_LogsAndStillRecordsGuardSuccessWithNoObservations()
    {
        // Mirrors Drps.Calculator's own Worker.cs precedent for an empty candidate list: the
        // cycle still reaches EarningsIngestionRunner.RunAsync (which no-ops cleanly on zero
        // tickers and still records the WorkerRunGuard success row for the day) rather than
        // special-casing an early skip that would leave today's guard unrecorded.
        var (scopeFactory, dbContext) = BuildScopeFactory(
            Array.Empty<string>(),
            _ => throw new InvalidOperationException("HTTP call should never have been attempted for an empty candidate list"));

        var logger = new CapturingLogger<EarningsWorker>();
        var worker = new EarningsWorker(
            logger, scopeFactory, JustBeforeThreeAm, new[] { "--run-now=Drps.Earnings" });

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        Assert.Empty(await dbContext.RawEarningsObservations.ToListAsync());
        Assert.Contains(logger.Messages, m => m.Contains("no DMA-5-aligned candidate ticker(s) found"));

        var hasRun = await WorkerRunGuard.HasRunTodayAsync(
            dbContext, "Drps.Earnings", DateOnly.FromDateTime(JustBeforeThreeAm()), CancellationToken.None);
        Assert.True(hasRun);
    }

    [Fact]
    public async Task ExecuteAsync_BareRunNowFlag_DoesNotTriggerManualRunAndEntersScheduledLoop()
    {
        // Same "no bare --run-now path" shape as RegimeWorker/SectorWorker/UniverseWorker - a
        // bare flag must fall straight through to the normal scheduled loop.
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var logger = new CapturingLogger<EarningsWorker>();

        var worker = new EarningsWorker(logger, scopeFactory, JustBeforeThreeAm, new[] { "--run-now" });

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (logger.Messages.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        Assert.Contains(logger.Messages, m => m.Contains("next scheduled run at"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("starting manual/on-demand run"));
    }

    // Stands in for "a fake RollingDmaStates table state" - the abstraction boundary
    // IDma5AlignedCandidateSource exists specifically so tests never need a real database
    // connection, same precedent as IFredCsvTransport/IGitCommandRunner elsewhere in this
    // codebase. SqlDma5AlignedCandidateSource's own raw-SQL correctness is verified by direct
    // code review against the confirmed RollingDmaStates schema (Ticker/Term/IsAligned/
    // CalculationVersion, per RollingDmaStateConfiguration.cs and the AddRollingDmaStateMachine
    // migration), not by an automated live-database test - consistent with this codebase's
    // existing precedent for Drps.Diagnostics/TickerBotVerificationProbe.cs's own raw SQL, which
    // carries no automated test coverage either.
    private sealed class FakeDma5AlignedCandidateSource : IDma5AlignedCandidateSource
    {
        private readonly IReadOnlyList<string> _tickers;

        public FakeDma5AlignedCandidateSource(IReadOnlyList<string> tickers) => _tickers = tickers;

        public Task<IReadOnlyList<string>> GetDma5AlignedTickersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_tickers);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();
        private readonly object _lock = new();

        public IReadOnlyList<string> Messages
        {
            get { lock (_lock) { return _entries.Select(e => e.Message).ToList(); } }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
