using Drps.Execution;
using Drps.Execution.Alpaca;
using Drps.Execution.Notifications;
using Drps.Execution.Reconciliation;
using Drps.Ingestion.Persistence;
using Drps.Ledger;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Execution;

public class ReconciliationWorkerTests
{
    private static ServiceProvider BuildProvider(string dbName, FakeAlpacaTradingClient client)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DrpsDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton<IAlpacaTradingClient>(client);
        services.AddSingleton<IPushoverNotificationService>(new FakePushoverNotificationService());
        services.AddScoped<LedgerPositionWriter>();
        services.AddScoped<ReconciliationService>();
        return services.BuildServiceProvider();
    }

    private static ReconciliationWorker CreateWorker(
        ServiceProvider provider,
        ILogger<ReconciliationWorker> logger,
        ReconciliationSettings? settings = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(settings ?? new ReconciliationSettings()),
            logger,
            // Instant delay - tests only exercise RunCycleAsync directly, never ExecuteAsync's
            // real between-cycle wait, same convention as OrchestrationWorker/
            // AtrRatchetMonitorWorker's own tests.
            delay: delay ?? ((_, _) => Task.CompletedTask));

    [Fact]
    public async Task RunCycleAsync_SuccessfulCycle_LogsCorrectCounts()
    {
        // One new orphan (no matching Ledger row, nothing yet on ExcludedTicker) and nothing
        // else - exercises every count field in ReconciliationResult flowing correctly into the
        // logged message.
        var client = new FakeAlpacaTradingClient
        {
            PositionsResult = _ => Task.FromResult(new AlpacaPositionsResult
            {
                Success = true,
                Positions = new[]
                {
                    new AlpacaPosition { Symbol = "ZZZZ", Quantity = 5m, AverageEntryPrice = 20m, MarketValue = 100m, Side = "long" }
                }
            })
        };
        using var provider = BuildProvider(Guid.NewGuid().ToString(), client);
        var logger = new CapturingLogger<ReconciliationWorker>();
        var worker = CreateWorker(provider, logger);

        await worker.RunCycleAsync(CancellationToken.None);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("cycle complete", message);
        Assert.Contains("orphans: 1 new / 0 already known", message);
        Assert.Contains("phantoms: 0 healed / 0 unresolved", message);
        Assert.Contains("discrepancies logged: 0", message);
    }

    [Fact]
    public async Task RunCycleAsync_TopLevelFailure_LogsAndDoesNotThrow()
    {
        // ReconciliationResult.Success = false (the top-level Alpaca open-positions fetch itself
        // failing) must be logged, not thrown - the caller's poll loop must survive this exactly
        // like an unhandled exception.
        var client = new FakeAlpacaTradingClient
        {
            PositionsResult = _ => Task.FromResult(new AlpacaPositionsResult
            {
                Success = false,
                ErrorMessage = "Alpaca API unavailable"
            })
        };
        using var provider = BuildProvider(Guid.NewGuid().ToString(), client);
        var logger = new CapturingLogger<ReconciliationWorker>();
        var worker = CreateWorker(provider, logger);

        // Does not throw.
        await worker.RunCycleAsync(CancellationToken.None);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("cycle failed at the top level", message);
        Assert.Contains("Alpaca API unavailable", message);
    }

    [Fact]
    public async Task RunCycleAsync_UnhandledException_LogsAndDoesNotThrow()
    {
        // A genuine unhandled exception (not a ReconciliationResult.Success = false) must also
        // be caught and logged, never propagated - same resilience requirement, different
        // failure shape.
        var client = new FakeAlpacaTradingClient
        {
            PositionsResult = _ => throw new InvalidOperationException("simulated unexpected failure")
        };
        using var provider = BuildProvider(Guid.NewGuid().ToString(), client);
        var logger = new CapturingLogger<ReconciliationWorker>();
        var worker = CreateWorker(provider, logger);

        await worker.RunCycleAsync(CancellationToken.None);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("unhandled exception this cycle", message);
    }

    [Fact]
    public async Task RunCycleAsync_CalledTwiceInSequence_BothCyclesExecuteSuccessfully()
    {
        // Confirms the worker's per-cycle logic can run repeatedly without corrupting state or
        // throwing - the same "two consecutive cycles" verification shape already used by
        // OrchestrationWorkerTests/AtrRatchetMonitorWorkerTests for this same flat-polling-loop
        // Worker family (ExecuteAsync's own while loop is a thin wrapper around this method plus
        // an injectable delay, per this class's own doc comment).
        var callCount = 0;
        var client = new FakeAlpacaTradingClient
        {
            PositionsResult = _ =>
            {
                callCount++;
                return Task.FromResult(new AlpacaPositionsResult { Success = true });
            }
        };
        using var provider = BuildProvider(Guid.NewGuid().ToString(), client);
        var logger = new CapturingLogger<ReconciliationWorker>();
        var worker = CreateWorker(provider, logger);

        await worker.RunCycleAsync(CancellationToken.None);
        await worker.RunCycleAsync(CancellationToken.None);

        Assert.Equal(2, callCount);
        Assert.Equal(2, logger.Messages.Count(m => m.Contains("cycle complete")));
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
            var message = formatter(state, exception);
            lock (_lock)
            {
                _messages.Add(message);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
