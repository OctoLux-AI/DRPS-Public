using Drps.Ingestion;
using Drps.Ingestion.Orchestration;
using Drps.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Drps.Tests;

public class ExDividendWorkerTests
{
    // Fixed instant just before 03:00 - NextRunTimeCalculator.GetNextRunTime resolves this to
    // "today's 03:00", a real but tiny (~50ms) delay each cycle. Fixed rather than derived
    // from the real clock so the test is deterministic and fast regardless of when it runs.
    private static readonly Func<DateTime> JustBeforeThreeAm = () => new DateTime(2026, 7, 15, 2, 59, 59, 950);

    [Fact]
    public async Task ExecuteAsync_SymbolThrowsUnhandledException_LogsErrorAndContinuesLoopToNextScheduledRun()
    {
        // ExDividendIngestionRunner is deliberately never registered, so resolving it inside
        // ExDividendWorker's per-symbol loop throws InvalidOperationException for every
        // symbol, every cycle - a real "unhandled exception during a scheduled run" that
        // must be logged and not stop the loop, so the next cycle's 03:00 timer still gets
        // scheduled. Same pattern as WorkerTests' equivalent test for the intraday Worker.
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var settings = new IngestionSettings();
        var watchlist = new WatchlistOptions { Symbols = new[] { "AAA", "BBB" } };

        // Deterministic cancellation handshake, replacing a poll-then-cancel race that flaked
        // under parallel test load. ExDividendWorker logs "next scheduled run at" as the very
        // last statement before each `await Task.Delay(delay, stoppingToken)` - since Log() runs
        // synchronously on the worker's own thread, blocking inside it on the 3rd occurrence
        // (i.e. after cycles 1 and 2 have both fully completed) pauses the worker at exactly
        // that point. The test then cancels the token and only *afterward* releases the worker,
        // so Task.Delay is guaranteed to observe an already-cancelled token and throw
        // deterministically - instead of racing cts.Cancel() against whichever phase of the
        // loop (foreach/logging/top-of-while-check) happens to be executing under load, which is
        // the actual race that caused the original flake: cancellation landing between the
        // per-symbol foreach loop (which never observes stoppingToken on this failure path) and
        // the next `while (!stoppingToken.IsCancellationRequested)` check let the loop exit
        // cleanly without ever re-entering Task.Delay, so no OperationCanceledException was ever
        // thrown for the test to catch.
        using var readyToCancel = new SemaphoreSlim(0, 1);
        using var releaseWorker = new SemaphoreSlim(0, 1);
        var nextRunLogCount = 0;
        var logger = new CapturingLogger<ExDividendWorker>(onMessageLogged: message =>
        {
            if (!message.Contains("next scheduled run at"))
            {
                return;
            }

            if (Interlocked.Increment(ref nextRunLogCount) == 3)
            {
                readyToCancel.Release();
                releaseWorker.Wait();
            }
        });

        var worker = new ExDividendWorker(
            logger, scopeFactory, Options.Create(settings), Options.Create(watchlist), JustBeforeThreeAm);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        await readyToCancel.WaitAsync();
        cts.Cancel();
        releaseWorker.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        // Proves the exact requirement: an exception during cycle 1 didn't prevent cycle 2's
        // timer from being scheduled - "starting scheduled run 2" only logs after a full
        // second pass through the delay-then-run loop.
        Assert.Contains(logger.Messages, m => m.Contains("starting scheduled run 1"));
        Assert.Contains(logger.Messages, m => m.Contains("starting scheduled run 2"));
        Assert.True(logger.ErrorCount >= 4, $"Expected at least 4 errors (2 symbols x 2 runs), got {logger.ErrorCount}");
        Assert.Contains(logger.Messages, m => m.Contains("ex-dividend run failed for AAA"));
        Assert.Contains(logger.Messages, m => m.Contains("ex-dividend run failed for BBB"));
    }

    [Fact]
    public async Task ExecuteAsync_StoppingTokenCancelledDuringSymbolProcessing_PropagatesAndStopsLoop()
    {
        // Simulates a real shutdown arriving mid-run: resolving ExDividendIngestionRunner is
        // where the cancellation is observed here. The boundary under test is
        // ExDividendWorker's own catch clause - it must not treat this as a data-fetching
        // failure regardless of where the exception originates, same requirement (#5) as the
        // intraday Worker.
        var services = new ServiceCollection();
        using var cts = new CancellationTokenSource();
        services.AddScoped<ExDividendIngestionRunner>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException("simulated shutdown mid-run", cts.Token);
        });
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var settings = new IngestionSettings();
        var watchlist = new WatchlistOptions { Symbols = new[] { "AAA", "BBB" } };

        var logger = new CapturingLogger<ExDividendWorker>();
        var worker = new ExDividendWorker(
            logger, scopeFactory, Options.Create(settings), Options.Create(watchlist), JustBeforeThreeAm);

        await worker.StartAsync(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        // Cancellation must propagate, not get logged-and-continued as if it were a
        // data-fetching failure - and the second symbol must never be attempted since the
        // loop stops immediately once the cancellation surfaces.
        Assert.DoesNotContain(logger.Messages, m => m.Contains("ex-dividend run failed"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("BBB"));
    }

    [Fact]
    public async Task ExecuteAsync_NoNowProviderSupplied_DefaultsToRealClockAndSchedulesAFutureRun()
    {
        // Confirms the production default (no nowProvider override) computes a real,
        // non-negative delay against the actual wall clock, rather than only working when a
        // test seam is supplied.
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var settings = new IngestionSettings();
        var watchlist = new WatchlistOptions { Symbols = Array.Empty<string>() };
        var logger = new CapturingLogger<ExDividendWorker>();
        var worker = new ExDividendWorker(logger, scopeFactory, Options.Create(settings), Options.Create(watchlist));

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
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();
        private readonly object _lock = new();
        private readonly Action<string>? _onMessageLogged;

        public CapturingLogger(Action<string>? onMessageLogged = null)
        {
            _onMessageLogged = onMessageLogged;
        }

        public IReadOnlyList<string> Messages
        {
            get { lock (_lock) { return _entries.Select(e => e.Message).ToList(); } }
        }

        public int ErrorCount
        {
            get { lock (_lock) { return _entries.Count(e => e.Level == LogLevel.Error); } }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            string message = formatter(state, exception);
            lock (_lock)
            {
                _entries.Add((logLevel, message));
            }

            // Deliberately invoked outside the lock: the callback (used by one test to pause the
            // worker's own thread for a deterministic cancellation handshake) must never be able
            // to block a thread that's holding this logger's lock, since the test thread still
            // needs to call Messages/ErrorCount while the worker thread is paused.
            _onMessageLogged?.Invoke(message);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
