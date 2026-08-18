using Drps.Ingestion;
using Drps.Ingestion.Orchestration;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Drps.Tests;

public class RegimeWorkerTests
{
    // Fixed instant just before 03:00 - NextRunTimeCalculator.GetNextRunTime resolves this to
    // "today's 03:00," a real but tiny (~50ms) delay each cycle. Same fixed-clock pattern as
    // every sibling Worker test in this codebase.
    private static readonly Func<DateTime> JustBeforeThreeAm = () => new DateTime(2026, 7, 15, 2, 59, 59, 950);

    [Fact]
    public async Task ExecuteAsync_NamedRunNowMatchesWorkerName_RunsOnceThenReturnsWithoutStoppingHostOrEnteringScheduledLoop()
    {
        // CLAUDE.md's "On-Demand Worker Targeting: --run-now Worker-Name Filter + Clean Exit"
        // (2026-07-27), corrected 2026-07-28 ("Named On-Demand Worker Targeting: Host-Wide
        // StopApplication() Corrected to Per-Worker Exit") - RegimeWorker is the class whose
        // named trigger produced the confirmed 2026-07-27 bug (a live --run-now=Drps.Regime
        // silently killed UniverseWorker's/UniverseBarSweepWorker's own pending scheduled runs
        // via the old host-wide StopApplication() call). No IRegimeFeeder is registered (an
        // empty IEnumerable<IRegimeFeeder> resolves fine - RegimeIngestionRunner's own loop
        // simply does nothing), so this exercises the real, unmodified
        // RegimeIngestionRunner.RunAsync end-to-end against a real in-memory DrpsDbContext.
        var services = new ServiceCollection();
        services.AddSingleton(InMemoryDbContextFactory.Create());
        services.AddScoped<RegimeIngestionRunner>();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<RegimeWorker>();

        var worker = new RegimeWorker(
            logger, scopeFactory, JustBeforeThreeAm, new[] { "--run-now=Drps.Regime" });

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        Assert.Contains(logger.Messages, m => m.Contains("starting manual/on-demand run"));
        Assert.Contains(logger.Messages, m => m.Contains("manual/on-demand run") && m.Contains("complete"));
        Assert.Contains(logger.Messages,
            m => m.Contains("named on-demand run for Drps.Regime complete") && m.Contains("execution ends here"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("stopping host"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("next scheduled run at"));
    }

    [Fact]
    public async Task ExecuteAsync_BareRunNowFlag_DoesNotTriggerManualRunAndEntersScheduledLoop()
    {
        // Unlike Drps.Ingestion's/Drps.Calculator's/Drps.Gate's own Worker classes, RegimeWorker
        // has no bare "--run-now" path at all (per this class's own doc comment) - a bare flag
        // must fall straight through to the normal scheduled loop, same as no flag being passed.
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<RegimeWorker>();

        var worker = new RegimeWorker(
            logger, scopeFactory, JustBeforeThreeAm, new[] { "--run-now" });

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

    [Fact]
    public async Task ExecuteAsync_NamedRunNowTargetsDifferentWorker_DoesNotTriggerManualRunAndEntersScheduledLoop()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<RegimeWorker>();

        var worker = new RegimeWorker(
            logger, scopeFactory, JustBeforeThreeAm, new[] { "--run-now=Drps.Sector" });

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

    [Fact]
    public async Task ExecuteAsync_NoNowProviderSupplied_DefaultsToRealClockAndSchedulesAFutureRun()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var logger = new CapturingLogger<RegimeWorker>();
        var worker = new RegimeWorker(logger, scopeFactory);

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
