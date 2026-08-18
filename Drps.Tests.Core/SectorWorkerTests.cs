using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Ingestion.Orchestration;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Drps.Shared.Scheduling;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests;

public class SectorWorkerTests
{
    // Fixed instant just before 03:00 - NextRunTimeCalculator.GetNextRunTime resolves this to
    // "today's 03:27" (SectorWorker's own RunTime = DailyRunTime + 27 minutes), a real but
    // tiny delay each cycle. Same fixed-clock pattern as EarningsWorkerTests'/RegimeWorkerTests'
    // own JustBeforeThreeAm fixtures.
    private static readonly Func<DateTime> JustBeforeThreeAm = () => new DateTime(2026, 8, 8, 2, 59, 59, 950);

    // Builds a real, minimal DI container wiring SectorIngestionRunner and a fake ISectorFeeder
    // against a single shared in-memory DrpsDbContext, so the whole Worker -> Runner -> Feeder
    // -> DB chain can be exercised end-to-end without a real network call or a real database.
    // ISectorFeeder has a real interface (unlike FinnhubEarningsFeeder, which has none), so a
    // plain fake implementation is used here rather than FinnhubSectorFeeder plus a fake HTTP
    // transport - simpler, and consistent with how this codebase already tests other
    // interface-backed feeders.
    //
    // IDma5AlignedCandidateSource is registered AddScoped here (matching production's own
    // registration lifetime, Program.cs) rather than passed directly to SectorWorker's
    // constructor - the DI-lifetime-violation fix (SectorWorker now resolves it via
    // IServiceScopeFactory.CreateScope() at the point of use, same as SectorIngestionRunner
    // already was) means the constructor no longer accepts it at all.
    private static (IServiceScopeFactory ScopeFactory, DrpsDbContext DbContext) BuildScopeFactory(
        IReadOnlyList<string> candidateTickers, Func<string, SectorFetchResult> respond)
    {
        var dbContext = InMemoryDbContextFactory.Create();
        var services = new ServiceCollection();
        services.AddSingleton(dbContext);
        // SectorIngestionRunner's own constructor needs ILogger<SectorIngestionRunner> via DI -
        // a bare ServiceCollection with no AddLogging() call cannot resolve that, and
        // GetRequiredService would throw. Registered directly as a no-op logger rather than
        // pulling in a full logging pipeline this test has no use for.
        services.AddSingleton<ILogger<SectorIngestionRunner>>(NullLogger<SectorIngestionRunner>.Instance);
        services.AddSingleton<ISectorFeeder>(new FakeSectorFeeder(respond));
        services.AddScoped<SectorIngestionRunner>();
        services.AddScoped<IDma5AlignedCandidateSource>(_ => new FakeDma5AlignedCandidateSource(candidateTickers));

        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IServiceScopeFactory>(), dbContext);
    }

    private static IngestionSettings BuildSettings() => new() { LookbackDays = 1 };

    [Fact]
    public async Task RunTime_Is27MinutesAfterTheSharedDailyDefault()
    {
        // CLAUDE.md's "FIX: Repoint SectorWorker to Calculator's dynamic DMA-5-aligned
        // candidate list" (2026-08-08) - retimed from 03:00 to 03:27 (DailyRunTime + 27
        // minutes), after Calculator's own 03:20 run and EarningsWorker's own 03:25 run.
        // Verified indirectly via the scheduled-loop's own "next scheduled run at" log line
        // rather than reflection against a private field, matching this codebase's existing
        // sibling Worker tests. Asserted via the logged "(in {Delay})" TimeSpan rather than the
        // "next scheduled run at {NextRunAt}" DateTime - TimeSpan's default ToString() is
        // culture-invariant, unlike DateTime's, which would otherwise make this assertion
        // fragile against whatever culture the test host happens to run under.
        var (scopeFactory, _) = BuildScopeFactory(Array.Empty<string>(), _ => new SectorFetchResult { Success = true });
        var logger = new CapturingLogger<SectorWorker>();

        var worker = new SectorWorker(
            logger, scopeFactory, Options.Create(BuildSettings()), JustBeforeThreeAm);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (logger.Messages.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        var expectedNextRunAt = NextRunTimeCalculator.GetNextRunTime(
            JustBeforeThreeAm(), NextRunTimeCalculator.DailyRunTime + TimeSpan.FromMinutes(27));
        var expectedDelay = expectedNextRunAt - JustBeforeThreeAm();
        var message = Assert.Single(logger.Messages, m => m.Contains("next scheduled run at"));
        Assert.Contains(expectedDelay.ToString(), message);
    }

    [Fact]
    public async Task ExecuteAsync_NamedRunNow_UsesCandidateSourceTickersNotAStaticWatchlist()
    {
        // The actual fix this task verifies: SectorWorker no longer has any dependency on
        // WatchlistOptions at all - the exact ticker set persisted below can only have come
        // from IDma5AlignedCandidateSource, since nothing else in this test wires a ticker
        // list in.
        var (scopeFactory, dbContext) = BuildScopeFactory(new[] { "AAPL", "MSFT" }, ticker => new SectorFetchResult
        {
            Success = true,
            Observations = new[]
            {
                new RawSectorObservation
                {
                    Ticker = ticker,
                    Source = SectorSourceType.Finnhub,
                    SectorValue = "Technology",
                    FetchedAt = DateTime.UtcNow,
                    Verified = true
                }
            }
        });

        var logger = new CapturingLogger<SectorWorker>();
        var worker = new SectorWorker(
            logger, scopeFactory, Options.Create(BuildSettings()), JustBeforeThreeAm,
            new[] { "--run-now=Drps.Sector" });

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        var persistedTickers = (await dbContext.RawSectorObservations.Select(r => r.Ticker).ToListAsync())
            .OrderBy(t => t)
            .ToList();
        Assert.Equal(new[] { "AAPL", "MSFT" }, persistedTickers);

        Assert.Contains(logger.Messages,
            m => m.Contains("named on-demand run for Drps.Sector complete") && m.Contains("execution ends here"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("stopping host"));
    }

    [Fact]
    public async Task ExecuteAsync_CandidateSourceReturnsEmptyList_LogsAndPersistsNoObservations()
    {
        var (scopeFactory, dbContext) = BuildScopeFactory(
            Array.Empty<string>(),
            _ => throw new InvalidOperationException("Feeder should never have been called for an empty candidate list"));

        var logger = new CapturingLogger<SectorWorker>();
        var worker = new SectorWorker(
            logger, scopeFactory, Options.Create(BuildSettings()), JustBeforeThreeAm,
            new[] { "--run-now=Drps.Sector" });

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        Assert.Empty(await dbContext.RawSectorObservations.ToListAsync());
        Assert.Contains(logger.Messages, m => m.Contains("no DMA-5-aligned candidate ticker(s) found"));
        Assert.Contains(logger.Messages, m => m.Contains("processed 0 ticker(s)"));
    }

    [Fact]
    public async Task ExecuteAsync_BareRunNowFlag_DoesNotTriggerManualRunAndEntersScheduledLoop()
    {
        // Same "no bare --run-now path" shape as RegimeWorker/EarningsWorker/UniverseWorker -
        // a bare flag must fall straight through to the normal scheduled loop.
        var (scopeFactory, _) = BuildScopeFactory(Array.Empty<string>(), _ => new SectorFetchResult { Success = true });
        var logger = new CapturingLogger<SectorWorker>();

        var worker = new SectorWorker(
            logger, scopeFactory, Options.Create(BuildSettings()), JustBeforeThreeAm,
            new[] { "--run-now" });

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
    // existing precedent for Drps.Diagnostics/TickerBotVerificationProbe.cs's own raw SQL, and
    // with EarningsWorkerTests' own identical fake (this class is not shared/reused from there
    // since each test file owns its own small fakes, per this codebase's existing convention).
    private sealed class FakeDma5AlignedCandidateSource : IDma5AlignedCandidateSource
    {
        private readonly IReadOnlyList<string> _tickers;

        public FakeDma5AlignedCandidateSource(IReadOnlyList<string> tickers) => _tickers = tickers;

        public Task<IReadOnlyList<string>> GetDma5AlignedTickersAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_tickers);
    }

    private sealed class FakeSectorFeeder : ISectorFeeder
    {
        private readonly Func<string, SectorFetchResult> _respond;

        public FakeSectorFeeder(Func<string, SectorFetchResult> respond) => _respond = respond;

        public SectorSourceType Source => SectorSourceType.Finnhub;

        public Task<SectorFetchResult> FetchSectorAsync(string ticker, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(ticker));
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
