using Drps.Ingestion.Orchestration;
using Drps.Shared.Scheduling;

namespace Drps.Ingestion;

/// <summary>
/// Same daily 03:00 cadence class as ExDividendWorker/SectorWorker/UniverseWorker - VIX/VXN/
/// VIX3M close-of-day levels are a once-daily class of data, not Worker's 15-minute intraday
/// cadence. No watchlist loop (unlike SectorWorker/ExDividendWorker) - regime data has
/// nothing to do with the stock watchlist; RegimeIngestionRunner.RunAsync sweeps all five
/// registered IRegimeFeeder instances (VIX/Cboe, VXN/Cboe, VXN/FRED, VIX3M/Cboe, VIX3M/FRED)
/// in one call, same shape as UniverseWorker's single global operation.
///
/// On-demand worker-name targeting (CLAUDE.md's "On-Demand Worker Targeting: --run-now
/// Worker-Name Filter + Clean Exit", 2026-07-27, corrected 2026-07-28 - see "Named On-Demand
/// Worker Targeting: Host-Wide StopApplication() Corrected to Per-Worker Exit" addendum):
/// unlike Drps.Ingestion's/Drps.Calculator's/Drps.Gate's own Worker classes, this class has no
/// bare "--run-now" path - it was never wired to the original manual-trigger decision
/// (2026-07-24) and that omission is exactly what this decision closes. Only
/// "--run-now=Drps.Regime" (matched against WorkerName below, the same identifier
/// RegimeIngestionRunner already uses for its own WorkerRunRecord rows) triggers a manual run
/// here - it reuses the exact same RunRegimeCycleAsync sequence the scheduled loop uses
/// (RegimeIngestionRunner.RunAsync owns its own idempotency guard internally, so there is
/// nothing to duplicate at this level), then ends only this Worker's own task.
///
/// Deliberately does NOT call IHostApplicationLifetime.StopApplication() - this class is
/// co-hosted alongside six other BackgroundServices in the same Drps.Ingestion process
/// (Worker/ExDividendWorker/SectorWorker/UniverseWorker/UniverseBarSweepWorker/
/// WeeklyVarianceAuditWorker). This is the class whose named trigger produced the confirmed
/// 2026-07-27 bug: a live `--run-now=Drps.Regime` invocation correctly ran this Worker's manual
/// cycle, then called the old host-wide StopApplication(), which silently tore down
/// UniverseWorker's and UniverseBarSweepWorker's own pending scheduled runs mid-wait - directly
/// visible in that day's log file, which ends at this Worker's own "stopping host" line with no
/// further activity from any sibling for the rest of the day.
/// </summary>
public class RegimeWorker : BackgroundService
{
    // Matches RegimeIngestionRunner's own private WorkerName constant - the identifier
    // --run-now=<WorkerName> must equal for this Worker's named-target path
    // (RunNowArgs.IsNamedRunNow) to fire.
    private const string WorkerName = "Drps.Regime";

    private readonly ILogger<RegimeWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<DateTime> _nowProvider;
    private readonly bool _runNowNamed;

    public RegimeWorker(
        ILogger<RegimeWorker> logger,
        IServiceScopeFactory scopeFactory,
        Func<DateTime>? nowProvider = null,
        string[]? args = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _nowProvider = nowProvider ?? (() => DateTime.Now);
        // No bare-flag check here (see this class's own doc comment) - RegimeWorker only ever
        // responds to its own named target.
        _runNowNamed = RunNowArgs.IsNamedRunNow(args, WorkerName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runNumber = 0;

        // Named on-demand trigger - runs once, before the scheduled loop is ever entered, then
        // ends only this Worker's own task so a single-worker on-demand invocation completes
        // cleanly without disturbing any co-hosted sibling's own scheduled wait (see this
        // class's own doc comment).
        if (_runNowNamed)
        {
            var manualRunDate = DateOnly.FromDateTime(_nowProvider());
            await RunRegimeCycleAsync(manualRunDate, isManual: true, runNumber: 0, stoppingToken);

            _logger.LogInformation(
                "[REGIME-WORKER]: named on-demand run for {WorkerName} complete - this Worker's execution ends here; " +
                "other co-hosted workers in this process are unaffected and continue on their own schedules",
                WorkerName);
            return;
        }

        // Ends only via stoppingToken cancellation (Ctrl+C, host shutdown) - never on its
        // own, same as every other daily Worker's loop in this codebase.
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _nowProvider();
            var nextRunAt = NextRunTimeCalculator.GetNextRunTime(now);
            var delay = nextRunAt - now;

            _logger.LogInformation(
                "[REGIME-WORKER]: next scheduled run at {NextRunAt} (in {Delay})", nextRunAt, delay);

            await Task.Delay(delay, stoppingToken);

            runNumber++;
            var runDate = DateOnly.FromDateTime(_nowProvider());

            await RunRegimeCycleAsync(runDate, isManual: false, runNumber, stoppingToken);
        }
    }

    // Shared by both the named on-demand trigger and the scheduled while loop above - only the
    // log wording branches on isManual, same convention as Worker.cs's RunIngestionCycleAsync.
    private async Task RunRegimeCycleAsync(DateOnly runDate, bool isManual, int runNumber, CancellationToken stoppingToken)
    {
        if (isManual)
        {
            _logger.LogInformation("[REGIME-WORKER]: starting manual/on-demand run for {RunDate}", runDate);
        }
        else
        {
            _logger.LogInformation("[REGIME-WORKER]: starting scheduled run {RunNumber}", runNumber);
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<RegimeIngestionRunner>();
            await runner.RunAsync(runDate, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A real shutdown request, not a data-fetching failure - must propagate so the
            // host actually stops, not get logged-and-continued like the catch below.
            throw;
        }
        catch (Exception ex)
        {
            // This run's failure must not crash the loop or block future days - logged,
            // then back to computing the next 03:00 target.
            _logger.LogError(ex, "[REGIME-WORKER]: regime ingestion run failed for {RunDate}", runDate);
        }

        if (isManual)
        {
            _logger.LogInformation(
                "[REGIME-WORKER]: manual/on-demand run for {RunDate} complete", runDate);
        }
        else
        {
            _logger.LogInformation(
                "[REGIME-WORKER]: scheduled run {RunNumber} complete for {RunDate}", runNumber, runDate);
        }
    }
}
