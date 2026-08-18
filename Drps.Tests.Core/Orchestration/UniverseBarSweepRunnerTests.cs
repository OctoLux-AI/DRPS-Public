using Drps.Ingestion.Feeders;
using Drps.Ingestion.Orchestration;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Orchestration;

public class UniverseBarSweepRunnerTests
{
    private static readonly DateOnly SnapshotDate = new(2026, 7, 22);
    private static readonly DateOnly Start = new(2026, 7, 17);
    private static readonly DateOnly End = new(2026, 7, 21);

    private static RawOhlcvBar MakeBar(string symbol) => new()
    {
        Source = SourceType.Alpaca,
        Symbol = symbol,
        Timestamp = new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero),
        Resolution = "1Day",
        Open = 100m,
        High = 101m,
        Low = 99m,
        Close = 100.5m,
        Volume = 1000,
        AdjustmentType = "raw",
        IngestedAt = DateTimeOffset.UtcNow,
        RequestId = Guid.NewGuid()
    };

    private static async Task SeedSnapshotAsync(Drps.Ingestion.Persistence.DrpsDbContext dbContext, DateOnly date, IEnumerable<string> tickers)
    {
        foreach (var ticker in tickers)
            dbContext.UniverseSnapshots.Add(new UniverseSnapshot { SnapshotDate = date, Ticker = ticker, Exchange = "NASDAQ" });
        await dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task RunAsync_NoSnapshotForDate_DoesNotCallFeederAndPersistsNothing()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var feeder = new FakeBatchBarFeeder(_ => new BatchFeedFetchResult { Success = true });
        var runner = new UniverseBarSweepRunner(feeder, dbContext, NullLogger<UniverseBarSweepRunner>.Instance);

        await runner.RunAsync(SnapshotDate, Start, End, CancellationToken.None);

        Assert.Empty(feeder.Calls);
        Assert.Empty(await dbContext.RawOhlcvBars.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_NoSnapshotForDate_DoesNotRecordSuccessInGuard()
    {
        // Zero Alpaca calls were made here - recording success would block a legitimate
        // later-today retry once the snapshot this sweep depends on actually lands.
        using var dbContext = InMemoryDbContextFactory.Create();
        var feeder = new FakeBatchBarFeeder(_ => new BatchFeedFetchResult { Success = true });
        var runner = new UniverseBarSweepRunner(feeder, dbContext, NullLogger<UniverseBarSweepRunner>.Instance);

        await runner.RunAsync(SnapshotDate, Start, End, CancellationToken.None);

        var hasRun = await WorkerRunGuard.HasRunTodayAsync(dbContext, "Drps.UniverseBarSweep", SnapshotDate, CancellationToken.None);
        Assert.False(hasRun);
    }

    [Fact]
    public async Task RunAsync_AlreadyRanTodayPerGuard_SkipsSweepEntirely()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedSnapshotAsync(dbContext, SnapshotDate, new[] { "AAPL", "MSFT" });
        await WorkerRunGuard.RecordSuccessfulRunAsync(
            dbContext, "Drps.UniverseBarSweep", SnapshotDate, new DateTime(2026, 7, 22, 3, 10, 0), CancellationToken.None);

        var feeder = new FakeBatchBarFeeder(symbols => new BatchFeedFetchResult
        {
            Success = true,
            BarsBySymbol = symbols.ToDictionary(s => s, s => (IReadOnlyList<RawOhlcvBar>)new[] { MakeBar(s) })
        });
        var runner = new UniverseBarSweepRunner(feeder, dbContext, NullLogger<UniverseBarSweepRunner>.Instance);

        await runner.RunAsync(SnapshotDate, Start, End, CancellationToken.None);

        // No feeder call at all - the guard check short-circuits before the snapshot is even
        // read, exactly the ~12,479-symbol re-hit this guard exists to prevent.
        Assert.Empty(feeder.Calls);
        Assert.Empty(await dbContext.RawOhlcvBars.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_GuardRecordExistsForADifferentDate_StillSweepsForNewDate()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedSnapshotAsync(dbContext, SnapshotDate, new[] { "AAPL" });
        await WorkerRunGuard.RecordSuccessfulRunAsync(
            dbContext, "Drps.UniverseBarSweep", SnapshotDate.AddDays(-1), new DateTime(2026, 7, 21, 3, 10, 0), CancellationToken.None);

        var feeder = new FakeBatchBarFeeder(symbols => new BatchFeedFetchResult
        {
            Success = true,
            BarsBySymbol = symbols.ToDictionary(s => s, s => (IReadOnlyList<RawOhlcvBar>)new[] { MakeBar(s) })
        });
        var runner = new UniverseBarSweepRunner(feeder, dbContext, NullLogger<UniverseBarSweepRunner>.Instance);

        await runner.RunAsync(SnapshotDate, Start, End, CancellationToken.None);

        Assert.Single(feeder.Calls);
        Assert.Equal(1, await dbContext.RawOhlcvBars.CountAsync());
    }

    [Fact]
    public async Task RunAsync_SweepCompletes_RecordsSuccessInGuardEvenWithBatchFailures()
    {
        // Recorded regardless of per-batch outcome - same "cycle reached its end" interpretation
        // used everywhere else in this codebase. A failed batch is logged and retried next
        // cycle; it must not itself withhold the guard record for the whole sweep.
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedSnapshotAsync(dbContext, SnapshotDate, new[] { "AAPL" });
        var feeder = new FakeBatchBarFeeder(_ => new BatchFeedFetchResult { Success = false, ErrorMessage = "503" });
        var runner = new UniverseBarSweepRunner(feeder, dbContext, NullLogger<UniverseBarSweepRunner>.Instance);

        await runner.RunAsync(SnapshotDate, Start, End, CancellationToken.None);

        var hasRun = await WorkerRunGuard.HasRunTodayAsync(dbContext, "Drps.UniverseBarSweep", SnapshotDate, CancellationToken.None);
        Assert.True(hasRun);
    }

    [Fact]
    public async Task RunAsync_EveryTickerInSnapshotGetsAttempt_NoEligibilityFilterApplied()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedSnapshotAsync(dbContext, SnapshotDate, new[] { "AAPL", "MSFT", "ZZZZ" });

        var feeder = new FakeBatchBarFeeder(symbols => new BatchFeedFetchResult
        {
            Success = true,
            BarsBySymbol = symbols.ToDictionary(s => s, s => (IReadOnlyList<RawOhlcvBar>)new[] { MakeBar(s) })
        });
        var runner = new UniverseBarSweepRunner(feeder, dbContext, NullLogger<UniverseBarSweepRunner>.Instance);

        await runner.RunAsync(SnapshotDate, Start, End, CancellationToken.None);

        Assert.Single(feeder.Calls);
        Assert.Equal(3, feeder.Calls[0].Count);
        Assert.Equal(3, await dbContext.RawOhlcvBars.CountAsync());
    }

    [Fact]
    public async Task RunAsync_SnapshotLargerThanBatchSize_SplitsIntoMultipleBatchCalls()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tickers = Enumerable.Range(0, UniverseBarSweepRunner.ConfirmedMaxBatchSize + 20)
            .Select(i => $"T{i:D5}")
            .ToList();
        await SeedSnapshotAsync(dbContext, SnapshotDate, tickers);

        var feeder = new FakeBatchBarFeeder(symbols => new BatchFeedFetchResult
        {
            Success = true,
            BarsBySymbol = symbols.ToDictionary(s => s, s => (IReadOnlyList<RawOhlcvBar>)new[] { MakeBar(s) })
        });
        var runner = new UniverseBarSweepRunner(feeder, dbContext, NullLogger<UniverseBarSweepRunner>.Instance);

        await runner.RunAsync(SnapshotDate, Start, End, CancellationToken.None);

        // 520 tickers at the confirmed 500-symbol ceiling must split into exactly two calls -
        // 500 + 20, not one oversized call.
        Assert.Equal(2, feeder.Calls.Count);
        Assert.Equal(UniverseBarSweepRunner.ConfirmedMaxBatchSize, feeder.Calls[0].Count);
        Assert.Equal(20, feeder.Calls[1].Count);
        Assert.Equal(tickers.Count, await dbContext.RawOhlcvBars.CountAsync());
    }

    [Fact]
    public async Task RunAsync_OneBatchFailsAnotherSucceeds_SurvivingBatchStillPersisted()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tickers = Enumerable.Range(0, UniverseBarSweepRunner.ConfirmedMaxBatchSize + 20)
            .Select(i => $"T{i:D5}")
            .ToList();
        await SeedSnapshotAsync(dbContext, SnapshotDate, tickers);

        var callCount = 0;
        var feeder = new FakeBatchBarFeeder(symbols =>
        {
            callCount++;
            // First batch (the full 500) fails; second (the remaining 20) succeeds - one
            // batch's failure must not abort the rest of the sweep.
            return callCount == 1
                ? new BatchFeedFetchResult { Success = false, ErrorMessage = "503 Service Unavailable" }
                : new BatchFeedFetchResult
                {
                    Success = true,
                    BarsBySymbol = symbols.ToDictionary(s => s, s => (IReadOnlyList<RawOhlcvBar>)new[] { MakeBar(s) })
                };
        });
        var runner = new UniverseBarSweepRunner(feeder, dbContext, NullLogger<UniverseBarSweepRunner>.Instance);

        var exception = await Record.ExceptionAsync(() => runner.RunAsync(SnapshotDate, Start, End, CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal(2, feeder.Calls.Count);
        Assert.Equal(20, await dbContext.RawOhlcvBars.CountAsync());
    }

    [Fact]
    public async Task RunAsync_BatchThrowsUnexpectedly_DoesNotAbortSweepAndOtherBatchStillPersisted()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tickers = Enumerable.Range(0, UniverseBarSweepRunner.ConfirmedMaxBatchSize + 20)
            .Select(i => $"T{i:D5}")
            .ToList();
        await SeedSnapshotAsync(dbContext, SnapshotDate, tickers);

        var callCount = 0;
        var feeder = new FakeBatchBarFeeder(symbols =>
        {
            callCount++;
            if (callCount == 1)
                throw new InvalidOperationException("boom");

            return new BatchFeedFetchResult
            {
                Success = true,
                BarsBySymbol = symbols.ToDictionary(s => s, s => (IReadOnlyList<RawOhlcvBar>)new[] { MakeBar(s) })
            };
        });
        var runner = new UniverseBarSweepRunner(feeder, dbContext, NullLogger<UniverseBarSweepRunner>.Instance);

        var exception = await Record.ExceptionAsync(() => runner.RunAsync(SnapshotDate, Start, End, CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal(20, await dbContext.RawOhlcvBars.CountAsync());
    }

    [Fact]
    public async Task RunAsync_TickerWithNoBarsReturned_IsNotPersistedButSweepContinues()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedSnapshotAsync(dbContext, SnapshotDate, new[] { "AAPL", "HALTED" });

        var feeder = new FakeBatchBarFeeder(_ => new BatchFeedFetchResult
        {
            Success = true,
            BarsBySymbol = new Dictionary<string, IReadOnlyList<RawOhlcvBar>>
            {
                ["AAPL"] = new[] { MakeBar("AAPL") }
                // "HALTED" deliberately absent - no valid bar for this ticker in the window.
            }
        });
        var runner = new UniverseBarSweepRunner(feeder, dbContext, NullLogger<UniverseBarSweepRunner>.Instance);

        await runner.RunAsync(SnapshotDate, Start, End, CancellationToken.None);

        var rows = await dbContext.RawOhlcvBars.ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("AAPL", row.Symbol);
    }

    [Fact]
    public async Task RunAsync_NoBarVerificationRowsAreEverCreated_SingleSourceTierByAbsence()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedSnapshotAsync(dbContext, SnapshotDate, new[] { "AAPL" });

        var feeder = new FakeBatchBarFeeder(symbols => new BatchFeedFetchResult
        {
            Success = true,
            BarsBySymbol = symbols.ToDictionary(s => s, s => (IReadOnlyList<RawOhlcvBar>)new[] { MakeBar(s) })
        });
        var runner = new UniverseBarSweepRunner(feeder, dbContext, NullLogger<UniverseBarSweepRunner>.Instance);

        await runner.RunAsync(SnapshotDate, Start, End, CancellationToken.None);

        // The nightly sweep is single-source by design (Two-Tier Verification Cost Model) -
        // it must never itself create a BarVerification row, since that would require a
        // second source's confirmation this pipeline deliberately does not have.
        Assert.Empty(await dbContext.BarVerifications.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_KnownPartialCoverage_LogsWarningButStillSweepsPresentTickersUnchanged()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedSnapshotAsync(dbContext, SnapshotDate, new[] { "AAPL" });
        dbContext.UniverseSnapshotCoverages.Add(new UniverseSnapshotCoverage
        {
            SnapshotDate = SnapshotDate,
            SourcesSucceeded = UniverseSourceCoverage.Nasdaq,
            RecordedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var feeder = new FakeBatchBarFeeder(symbols => new BatchFeedFetchResult
        {
            Success = true,
            BarsBySymbol = symbols.ToDictionary(s => s, s => (IReadOnlyList<RawOhlcvBar>)new[] { MakeBar(s) })
        });
        var logger = new CapturingLogger<UniverseBarSweepRunner>();
        var runner = new UniverseBarSweepRunner(feeder, dbContext, logger);

        await runner.RunAsync(SnapshotDate, Start, End, CancellationToken.None);

        // Visibility only, not a behavior gate - the ticker(s) actually present are still swept
        // exactly as a full-coverage day would be.
        Assert.Equal(1, await dbContext.RawOhlcvBars.CountAsync());

        Assert.Contains(logger.Messages, m => m.Contains("KNOWN-PARTIAL") && m.Contains("Nasdaq"));
    }

    [Fact]
    public async Task RunAsync_FullCoverage_DoesNotLogPartialWarning()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedSnapshotAsync(dbContext, SnapshotDate, new[] { "AAPL" });
        dbContext.UniverseSnapshotCoverages.Add(new UniverseSnapshotCoverage
        {
            SnapshotDate = SnapshotDate,
            SourcesSucceeded = UniverseSourceCoverage.All,
            RecordedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var feeder = new FakeBatchBarFeeder(symbols => new BatchFeedFetchResult
        {
            Success = true,
            BarsBySymbol = symbols.ToDictionary(s => s, s => (IReadOnlyList<RawOhlcvBar>)new[] { MakeBar(s) })
        });
        var logger = new CapturingLogger<UniverseBarSweepRunner>();
        var runner = new UniverseBarSweepRunner(feeder, dbContext, logger);

        await runner.RunAsync(SnapshotDate, Start, End, CancellationToken.None);

        Assert.DoesNotContain(logger.Messages, m => m.Contains("KNOWN-PARTIAL"));
    }

    [Fact]
    public async Task RunAsync_NoCoverageRowForDate_DoesNotLogPartialWarningAndStillSweeps()
    {
        // A date predating this feature (or any other reason a coverage row was never written)
        // must fail open to the existing behavior - no warning, sweep proceeds normally.
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedSnapshotAsync(dbContext, SnapshotDate, new[] { "AAPL" });

        var feeder = new FakeBatchBarFeeder(symbols => new BatchFeedFetchResult
        {
            Success = true,
            BarsBySymbol = symbols.ToDictionary(s => s, s => (IReadOnlyList<RawOhlcvBar>)new[] { MakeBar(s) })
        });
        var logger = new CapturingLogger<UniverseBarSweepRunner>();
        var runner = new UniverseBarSweepRunner(feeder, dbContext, logger);

        await runner.RunAsync(SnapshotDate, Start, End, CancellationToken.None);

        Assert.Equal(1, await dbContext.RawOhlcvBars.CountAsync());
        Assert.DoesNotContain(logger.Messages, m => m.Contains("KNOWN-PARTIAL"));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = new();
        private readonly object _lock = new();

        public IReadOnlyList<string> Messages
        {
            get { lock (_lock) { return _messages.ToList(); } }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
            {
                _messages.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class FakeBatchBarFeeder : IAlpacaBatchBarFeeder
    {
        private readonly Func<IReadOnlyList<string>, BatchFeedFetchResult> _handler;

        public FakeBatchBarFeeder(Func<IReadOnlyList<string>, BatchFeedFetchResult> handler) => _handler = handler;

        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Task<BatchFeedFetchResult> FetchBatchAsync(
            IReadOnlyList<string> symbols, DateOnly start, DateOnly end, CancellationToken cancellationToken)
        {
            Calls.Add(symbols);
            return Task.FromResult(_handler(symbols));
        }
    }
}
