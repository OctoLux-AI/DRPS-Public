using Drps.Ingestion;
using Drps.Ingestion.Orchestration;
using Drps.Ingestion.Persistence;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Drps.Tests;

public class WeeklyVarianceAuditWorkerTests
{
    // 2026-07-18 is a Saturday, confirmed against the calendar (2026-01-01 is a Thursday).
    // Fixed instant just before 04:00 - NextRunTimeCalculator.GetNextWeeklyRunTime resolves this
    // to "this Saturday's 04:00," a real but tiny (~50ms) delay each cycle, same fixed-clock
    // trick as every sibling Worker test in this codebase.
    private static readonly Func<DateTime> JustBeforeSaturdayFourAm = () => new DateTime(2026, 7, 18, 3, 59, 59, 950);

    // The Friday this fixed clock's week ends on - what WeeklyVarianceAuditWorker resolves via
    // NextRunTimeCalculator.GetMostRecentOccurrence(runDate, DayOfWeek.Friday).
    private static readonly DateOnly ExpectedWeekEndingDate = new(2026, 7, 17);

    [Fact]
    public async Task ExecuteAsync_ServiceUnresolvable_LogsErrorAndContinuesLoopToNextScheduledRun()
    {
        // WeeklyVarianceAuditService is deliberately never registered, so resolving it inside
        // the Worker's per-run scope throws InvalidOperationException on every scheduled run -
        // this must be logged and must not stop the loop, so the next cycle's timer still gets
        // scheduled. DrpsDbContext IS registered (in-memory) so the idempotency guard itself
        // succeeds and isn't what's under test here.
        var services = new ServiceCollection();
        services.AddDbContext<DrpsDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        // Deterministic cancellation handshake, same pattern as GateWorkerTests/
        // ExDividendWorkerTests' equivalent tests - avoids a poll-then-cancel race under
        // parallel test load.
        using var readyToCancel = new SemaphoreSlim(0, 1);
        using var releaseWorker = new SemaphoreSlim(0, 1);
        var nextRunLogCount = 0;
        var logger = new CapturingLogger<WeeklyVarianceAuditWorker>(onMessageLogged: message =>
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

        var worker = new WeeklyVarianceAuditWorker(logger, scopeFactory, JustBeforeSaturdayFourAm);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        await readyToCancel.WaitAsync();
        cts.Cancel();
        releaseWorker.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        // Proves a resolution failure during cycle 1 didn't prevent cycle 2's timer from being
        // scheduled - "starting scheduled run 2" only logs after a full second pass through the
        // delay-then-run loop.
        Assert.Contains(logger.Messages, m => m.Contains("starting scheduled run 1"));
        Assert.Contains(logger.Messages, m => m.Contains("starting scheduled run 2"));
        Assert.True(logger.ErrorCount >= 2, $"Expected at least 2 errors (1 per run x 2 runs), got {logger.ErrorCount}");
        Assert.Contains(logger.Messages, m => m.Contains($"audit run failed for week ending {ExpectedWeekEndingDate:yyyy-MM-dd}"));
    }

    [Fact]
    public async Task ExecuteAsync_StoppingTokenCancelledDuringServiceResolution_PropagatesAndStopsLoop()
    {
        // Simulates a real shutdown arriving mid-run - the boundary under test is Worker's own
        // catch clause, which must not treat this as an audit-run failure regardless of where
        // the exception originates.
        var services = new ServiceCollection();
        services.AddDbContext<DrpsDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        using var cts = new CancellationTokenSource();
        services.AddScoped<WeeklyVarianceAuditService>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException("simulated shutdown mid-run", cts.Token);
        });
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<WeeklyVarianceAuditWorker>();
        var worker = new WeeklyVarianceAuditWorker(logger, scopeFactory, JustBeforeSaturdayFourAm);

        await worker.StartAsync(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        Assert.DoesNotContain(logger.Messages, m => m.Contains("audit run failed"));
    }

    [Fact]
    public async Task ExecuteAsync_NoNowProviderSupplied_DefaultsToRealClockAndSchedulesAFutureRun()
    {
        // Confirms the production default (no nowProvider override) computes a real,
        // non-negative delay against the actual wall clock.
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var logger = new CapturingLogger<WeeklyVarianceAuditWorker>();
        var worker = new WeeklyVarianceAuditWorker(logger, scopeFactory);

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
    public async Task ExecuteAsync_AlreadyRanSuccessfullyThisWeek_SkipsScheduledRunWithoutAttemptingWork()
    {
        // If the guard incorrectly let this proceed, that would surface as an "audit run
        // failed" error log exactly like the resolution-failure test above (WeeklyVarianceAuditService
        // is deliberately left unregistered here too) - its absence is the actual proof the
        // guard worked, not just that the "already ran" log appeared.
        using var dbContext = InMemoryDbContextFactory.Create();
        await WorkerRunGuard.RecordSuccessfulRunAsync(
            dbContext, "Drps.WeeklyVarianceAudit", ExpectedWeekEndingDate, new DateTime(2026, 7, 18, 4, 0, 0), CancellationToken.None);

        var services = new ServiceCollection();
        services.AddSingleton(dbContext);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<WeeklyVarianceAuditWorker>();
        var worker = new WeeklyVarianceAuditWorker(logger, scopeFactory, JustBeforeSaturdayFourAm);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!logger.Messages.Any(m => m.Contains("already ran successfully")) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        Assert.Contains(logger.Messages, m => m.Contains($"already ran successfully for week ending {ExpectedWeekEndingDate:yyyy-MM-dd}") && m.Contains("skipping scheduled run"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("audit run failed"));
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

            _onMessageLogged?.Invoke(message);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
