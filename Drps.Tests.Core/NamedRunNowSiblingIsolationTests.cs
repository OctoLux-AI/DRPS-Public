using Drps.Ingestion;
using Drps.Ingestion.Orchestration;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Drps.Tests;

/// <summary>
/// CLAUDE.md's "Named On-Demand Worker Targeting: Host-Wide StopApplication() Corrected to
/// Per-Worker Exit" (2026-07-28) - the requirement this task added on top of the individual
/// per-class fixes: proof, not just an updated assertion on one class in isolation, that a
/// named on-demand run of one Drps.Ingestion-hosted worker does not interrupt a co-hosted
/// sibling's own pending scheduled run.
///
/// Reproduces, at the unit level, the exact confirmed 2026-07-27 production bug: a named
/// --run-now=Drps.Regime trigger correctly ran RegimeWorker's manual cycle, but the old
/// implementation then called the host-wide IHostApplicationLifetime.StopApplication(), which
/// silently killed UniverseWorker's own pending wait for its next 03:00 scheduled run -
/// directly confirmed via that day's log file, which ended at RegimeWorker's own "stopping
/// host" line with zero further activity from any sibling worker for the rest of the day.
/// RegimeWorker and UniverseWorker are used here specifically because they are the two classes
/// actually involved in that real incident, not an arbitrary pair.
///
/// Post-fix, neither class holds an IHostApplicationLifetime or calls StopApplication() at all
/// (see each class's own doc comment) - the mechanism that caused the bug no longer exists in
/// either class. This test proves the corrected, observable outcome directly: RegimeWorker's
/// named run completes and returns entirely on its own, while UniverseWorker - constructed and
/// started independently but sharing this test's single CancellationTokenSource, the same
/// shape every co-hosted BackgroundService in a real Drps.Ingestion process shares via the
/// host's own lifetime-derived stoppingToken - remains alive and still actively waiting on its
/// own scheduled run throughout, only ending when this test explicitly cancels the shared
/// token afterward.
/// </summary>
public class NamedRunNowSiblingIsolationTests
{
    // Fixed instant just before 03:00 - NextRunTimeCalculator.GetNextRunTime resolves this to
    // "today's 03:00", a real but tiny (~50ms) delay each cycle. Same fixed-clock pattern as
    // every sibling Worker test in this codebase.
    private static readonly Func<DateTime> JustBeforeThreeAm = () => new DateTime(2026, 7, 15, 2, 59, 59, 950);

    [Fact]
    public async Task NamedRunNow_OneCoHostedWorkerCompletes_DoesNotInterruptSiblingWorkersPendingScheduledRun()
    {
        // UniverseWorker started first, and its own "next scheduled run at" log line is
        // confirmed before RegimeWorker's named run ever begins - this makes the causality
        // unambiguous: UniverseWorker was already alive and waiting when RegimeWorker's manual
        // run fired, not merely started afterward and never checked.
        var universeProvider = new ServiceCollection().BuildServiceProvider();
        var universeScopeFactory = universeProvider.GetRequiredService<IServiceScopeFactory>();
        var universeLogger = new CapturingLogger<UniverseWorker>();
        // No --run-now flag at all - normal persistent-scheduler mode, exactly like every
        // co-hosted worker that was NOT named in the real 2026-07-27 incident.
        var universeWorker = new UniverseWorker(universeLogger, universeScopeFactory, JustBeforeThreeAm);

        using var cts = new CancellationTokenSource();
        await universeWorker.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (universeLogger.Messages.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Contains(universeLogger.Messages, m => m.Contains("next scheduled run at"));
        Assert.False(universeWorker.ExecuteTask!.IsCompleted, "UniverseWorker should be waiting on its scheduled run, not completed.");

        // RegimeWorker: named on-demand run, sharing the SAME CancellationTokenSource -
        // exactly as it would share one Drps.Ingestion host's stoppingToken with UniverseWorker
        // in production.
        var regimeServices = new ServiceCollection();
        regimeServices.AddSingleton(InMemoryDbContextFactory.Create());
        regimeServices.AddScoped<RegimeIngestionRunner>();
        var regimeProvider = regimeServices.BuildServiceProvider();
        var regimeScopeFactory = regimeProvider.GetRequiredService<IServiceScopeFactory>();

        var regimeLogger = new CapturingLogger<RegimeWorker>();
        var regimeWorker = new RegimeWorker(
            regimeLogger, regimeScopeFactory, JustBeforeThreeAm, new[] { "--run-now=Drps.Regime" });

        await regimeWorker.StartAsync(cts.Token);

        // RegimeWorker's named run completes entirely on its own - no cancellation needed,
        // same as every other named-run-now test in this codebase.
        await regimeWorker.ExecuteTask!;

        Assert.Contains(regimeLogger.Messages,
            m => m.Contains("named on-demand run for Drps.Regime complete") && m.Contains("execution ends here"));
        Assert.DoesNotContain(regimeLogger.Messages, m => m.Contains("stopping host"));

        // The actual proof: UniverseWorker's own task is still running - Task.IsCompleted is
        // true for RanToCompletion, Faulted, AND Canceled alike, so "still false" here rules
        // out all three, meaning nothing (in particular, no cancellation) reached
        // UniverseWorker's stoppingToken as a side effect of RegimeWorker's named run and
        // return moments earlier. The shared CancellationTokenSource was never touched by
        // RegimeWorker's named-run path (it holds no IHostApplicationLifetime at all post-fix),
        // so UniverseWorker keeps cycling through its own scheduled loop completely undisturbed
        // - it may have already completed one or more of its own (JustBeforeThreeAm-driven,
        // sub-second) cycles by this point, which is itself further evidence it was never
        // interrupted, not a sign anything went wrong.
        Assert.False(universeWorker.ExecuteTask!.IsCompleted,
            "UniverseWorker's pending scheduled run must not be interrupted by a sibling worker's named on-demand completion");
        Assert.Contains(universeLogger.Messages, m => m.Contains("next scheduled run at"));

        // Clean shutdown for the still-running worker.
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => universeWorker.ExecuteTask!);
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
