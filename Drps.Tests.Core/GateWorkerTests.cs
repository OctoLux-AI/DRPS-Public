using Drps.Gate;
using Drps.Gate.Scoring;
using Drps.Ingestion.Orchestration;
using Drps.Ingestion.Persistence;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Drps.Tests;

public class GateWorkerTests
{
    // Fixed instant just before 03:35 - NextRunTimeCalculator.GetNextRunTime(now, 03:35)
    // resolves this to "today's 03:35," a real but tiny (~50ms) delay each cycle. The specific
    // target minute (03:35, not 03:00 or 03:20) also proves this Worker is actually using its
    // own ScheduledRunTime: if it silently defaulted to an earlier constant, this fixed clock
    // (already past that earlier time) would compute a delay to tomorrow instead, and "starting
    // scheduled run"/cancellation would never be observed within the test's deadline.
    private static readonly Func<DateTime> JustBeforeThreeThirtyFive = () => new DateTime(2026, 7, 15, 3, 34, 59, 950);

    [Fact]
    public async Task ExecuteAsync_ScanServiceUnresolvable_LogsErrorAndContinuesLoopToNextScheduledRun()
    {
        // Exception-boundary fix (2026-07-19): GateScanService resolution used to sit OUTSIDE
        // the try/catch, unlike Drps.Ingestion's/Drps.Calculator's Workers, which resolve their
        // per-run dependencies INSIDE their own try/catch - a DI resolution failure here used to
        // fault the whole ExecuteAsync task instead of being logged-and-continued. Now moved
        // inside the try block to match that existing pattern exactly. GateScanService is
        // deliberately never registered, so resolving it throws InvalidOperationException on
        // every scheduled run - this must be logged and must not stop the loop, so the next
        // cycle's 03:35 timer still gets scheduled. Same pattern as Drps.Ingestion's/
        // Drps.Calculator's equivalent tests for the equivalent failure mode.
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        // Deterministic cancellation handshake, replacing a poll-then-cancel race that flaked
        // under parallel test load (same fix as ExDividendWorkerTests' equivalent test - this
        // exact test was independently confirmed to flake under a 10x parallel full-suite stress
        // run before this fix). Worker logs "next scheduled run at" as the very last statement
        // before each `await Task.Delay(delay, stoppingToken)` - since Log() runs synchronously
        // on the worker's own thread, blocking inside it on the 3rd occurrence (i.e. after
        // cycles 1 and 2 have both fully completed) pauses the worker at exactly that point. The
        // test then cancels the token and only *afterward* releases the worker, so Task.Delay is
        // guaranteed to observe an already-cancelled token and throw deterministically - instead
        // of racing cts.Cancel() against whichever phase of the loop (idempotency guard/scan
        // resolution/logging/top-of-while-check) happens to be executing under load, which is
        // the actual race that caused the original flake: cancellation landing anywhere in that
        // per-cycle work (none of which observes stoppingToken on this failure path) let the loop
        // exit cleanly at the next
        // `while (!stoppingToken.IsCancellationRequested)` check without ever re-entering
        // Task.Delay, so no OperationCanceledException was ever thrown for the test to catch.
        using var readyToCancel = new SemaphoreSlim(0, 1);
        using var releaseWorker = new SemaphoreSlim(0, 1);
        var nextRunLogCount = 0;
        var logger = new CapturingLogger<Worker>(onMessageLogged: message =>
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

        var worker = new Worker(logger, scopeFactory, JustBeforeThreeThirtyFive);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        await readyToCancel.WaitAsync();
        cts.Cancel();
        releaseWorker.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        // Proves the exact requirement: a resolution failure during cycle 1 didn't prevent
        // cycle 2's timer from being scheduled - "starting scheduled run 2" only logs after a
        // full second pass through the delay-then-run loop.
        Assert.Contains(logger.Messages, m => m.Contains("starting scheduled run 1"));
        Assert.Contains(logger.Messages, m => m.Contains("starting scheduled run 2"));
        Assert.True(logger.ErrorCount >= 2, $"Expected at least 2 errors (1 per run x 2 runs), got {logger.ErrorCount}");
        Assert.Contains(logger.Messages, m => m.Contains("scheduled run 1 failed"));
        Assert.Contains(logger.Messages, m => m.Contains("scheduled run 2 failed"));
    }

    [Fact]
    public async Task ExecuteAsync_StoppingTokenCancelledDuringScan_PropagatesAndStopsLoop()
    {
        // Simulates a real shutdown arriving mid-run: resolving GateScanService (now inside the
        // try block, per the exception-boundary fix above) is where the cancellation is observed
        // here. The boundary under test is Worker's own catch clause - it must not treat this as
        // a scan failure regardless of where the exception originates, same requirement as
        // Drps.Ingestion's/Drps.Calculator's Workers.
        var services = new ServiceCollection();
        using var cts = new CancellationTokenSource();
        services.AddScoped<GateScanService>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException("simulated shutdown mid-run", cts.Token);
        });
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<Worker>();
        var worker = new Worker(logger, scopeFactory, JustBeforeThreeThirtyFive);

        await worker.StartAsync(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        // Cancellation must propagate, not get logged-and-continued as if it were a scan
        // failure.
        Assert.DoesNotContain(logger.Messages, m => m.Contains("scheduled run") && m.Contains("failed"));
    }

    [Fact]
    public async Task ExecuteAsync_NoNowProviderSupplied_DefaultsToRealClockAndSchedulesAFutureRun()
    {
        // Confirms the production default (no nowProvider override) computes a real,
        // non-negative delay against the actual wall clock, rather than only working when a
        // test seam is supplied.
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var logger = new CapturingLogger<Worker>();
        var worker = new Worker(logger, scopeFactory);

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

    [Fact]
    public async Task ExecuteAsync_AlreadyRanSuccessfullyToday_SkipsScheduledRunWithoutAttemptingWork()
    {
        // Idempotency guard (this session's scheduling-resilience audit, Fix 3) - same shape
        // as Drps.Ingestion's/Drps.Calculator's equivalent tests. GateScanService is
        // deliberately left unregistered, matching the resolution-failure test above - if the
        // guard incorrectly let this run proceed, that would surface as a "scheduled run
        // failed" error log exactly like that test; its absence here is the actual proof the
        // guard worked, not just that the "already ran" log appeared.
        using var dbContext = InMemoryDbContextFactory.Create();
        var today = new DateOnly(2026, 7, 15);
        await WorkerRunGuard.RecordSuccessfulRunAsync(
            dbContext, "Drps.Gate", today, new DateTime(2026, 7, 15, 3, 35, 0), CancellationToken.None);

        var services = new ServiceCollection();
        services.AddSingleton(dbContext);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<Worker>();
        var worker = new Worker(logger, scopeFactory, JustBeforeThreeThirtyFive);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!logger.Messages.Any(m => m.Contains("already ran successfully")) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        Assert.Contains(logger.Messages, m => m.Contains("already ran successfully for") && m.Contains("skipping scheduled run"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("scheduled run") && m.Contains("failed"));
    }

    [Fact]
    public async Task ExecuteAsync_NamedRunNowMatchesWorkerName_RunsOnceThenStopsHostWithoutEnteringScheduledLoop()
    {
        // CLAUDE.md's "On-Demand Worker Targeting: --run-now Worker-Name Filter + Clean Exit"
        // (2026-07-27). GateScanService is deliberately unregistered so the manual run logs an
        // error - what's under test is that the manual run fires immediately, StopApplication is
        // called exactly once, and the scheduled while loop is never entered.
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<Worker>();
        var hostLifetime = new FakeHostApplicationLifetime();

        var worker = new Worker(
            logger, scopeFactory, JustBeforeThreeThirtyFive, hostLifetime, new[] { "--run-now=Drps.Gate" });

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        Assert.Contains(logger.Messages, m => m.Contains("starting manual/on-demand run"));
        Assert.Contains(logger.Messages, m => m.Contains("named on-demand run for Drps.Gate complete - stopping host"));
        Assert.Equal(1, hostLifetime.StopApplicationCallCount);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("next scheduled run at"));
    }

    [Fact]
    public async Task ExecuteAsync_NamedRunNowTargetsDifferentWorker_DoesNotTriggerManualRunAndEntersScheduledLoop()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<Worker>();
        var hostLifetime = new FakeHostApplicationLifetime();

        var worker = new Worker(
            logger, scopeFactory, JustBeforeThreeThirtyFive, hostLifetime, new[] { "--run-now=Drps.Calculator" });

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
        Assert.Equal(0, hostLifetime.StopApplicationCallCount);
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
