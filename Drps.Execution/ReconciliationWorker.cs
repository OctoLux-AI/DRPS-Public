using Drps.Execution.Reconciliation;
using Microsoft.Extensions.Options;

namespace Drps.Execution;

/// <summary>
/// The BackgroundService wrapping ReconciliationService (CLAUDE.md's Execution Layer: Fifth
/// Design Decision) on its own periodic cadence - the last piece of the entire Execution Layer
/// scope opened this session. A genuinely slower, independent cadence from both
/// OrchestrationWorker's 60s candidate-discovery loop and AtrRatchetMonitorWorker's 120s tight
/// stop-breach loop (ReconciliationSettings.PollIntervalSeconds, 300s default) - drift detection
/// between Drps.Ledger's believed state and Alpaca's real account state isn't a sub-minute
/// problem.
///
/// Each poll cycle is a single ReconciliationService.RunAsync call - no per-item detached tasks
/// the way OrchestrationWorker/AtrRatchetMonitorWorker need, since nothing here fires an order or
/// waits on a fill. There is no other consumer of ReconciliationResult's counts yet, so this
/// Worker's whole job is making them genuinely visible in logs - a human is the consumer today.
///
/// A single cycle's failure (an unhandled exception, or ReconciliationResult.Success = false from
/// the top-level Alpaca open-positions fetch failing) is logged and never crashes the loop - same
/// resilience pattern every other Worker in this codebase already applies to its own per-cycle
/// work.
/// </summary>
public class ReconciliationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<ReconciliationSettings> _settings;
    private readonly ILogger<ReconciliationWorker> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public ReconciliationWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<ReconciliationSettings> settings,
        ILogger<ReconciliationWorker> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _logger = logger;
        // Injectable so tests can make the between-cycle wait resolve instantly - same seam
        // pattern as OrchestrationWorker/AtrRatchetMonitorWorker's own injectable delay.
        _delay = delay ?? Task.Delay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ends only via stoppingToken cancellation (Ctrl+C, host shutdown) - never on its own,
        // same as every other Worker in this codebase.
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCycleAsync(stoppingToken);
            await _delay(TimeSpan.FromSeconds(_settings.Value.PollIntervalSeconds), stoppingToken);
        }
    }

    /// <summary>
    /// Runs one full reconciliation pass and logs its outcome. Never throws for a
    /// ReconciliationService-level failure (top-level Success = false) or an unhandled
    /// exception - both are logged and swallowed here so the caller's poll loop continues.
    /// </summary>
    public async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();

        try
        {
            var reconciliationService = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
            var result = await reconciliationService.RunAsync(stoppingToken);

            if (!result.Success)
            {
                _logger.LogError(
                    "[RECONCILIATION-WORKER]: cycle failed at the top level - {ErrorMessage} - will retry next poll",
                    result.ErrorMessage);
                return;
            }

            _logger.LogInformation(
                "[RECONCILIATION-WORKER]: cycle complete - orphans: {NewOrphans} new / {AlreadyKnownOrphans} " +
                "already known, phantoms: {PhantomsHealed} healed / {PhantomsUnresolved} unresolved, " +
                "discrepancies logged: {DiscrepanciesLogged}",
                result.NewOrphansDetected, result.AlreadyKnownOrphans,
                result.PhantomsHealed, result.PhantomsUnresolved, result.DiscrepanciesLogged);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No single cycle's failure may crash this loop - same discipline as
            // OrchestrationWorker/AtrRatchetMonitorWorker's own top-level per-cycle catch.
            _logger.LogError(ex, "[RECONCILIATION-WORKER]: unhandled exception this cycle - will retry next poll");
        }
    }
}
