using Drps.Ingestion.Orchestration;
using Drps.Shared.Scheduling;

namespace Drps.Ingestion;

/// <summary>
/// Nightly full-universe OHLCV bar pull - see UniverseBarSweepRunner's own doc comment for
/// what it does and why. Runs at 03:10, ten minutes after UniverseWorker's 03:00 snapshot
/// refresh - a real time buffer, not a completion guarantee, same staggering precedent
/// CLAUDE.md already documents across Ingestion/Calculator/Gate's own daily loops, applied
/// here between two daily loops within Ingestion itself since this one has a hard dependency
/// on the other's output. If that day's UniverseSnapshot genuinely isn't ready yet (a slow or
/// failed run), UniverseBarSweepRunner logs a warning and does nothing - it does not wait or
/// retry mid-cycle; the next scheduled day picks it back up.
///
/// On-demand worker-name targeting (CLAUDE.md's "On-Demand Worker Targeting: --run-now
/// Worker-Name Filter + Clean Exit", 2026-07-27, corrected 2026-07-28 - see "Named On-Demand
/// Worker Targeting: Host-Wide StopApplication() Corrected to Per-Worker Exit" addendum): this
/// class has no bare "--run-now" path, same as RegimeWorker - only
/// "--run-now=Drps.UniverseBarSweep" (matched against WorkerName below, the same identifier
/// UniverseBarSweepRunner already uses for its own WorkerRunRecord rows) triggers a manual run.
/// UniverseBarSweepRunner owns its own idempotency guard internally (and its own
/// dependency-not-ready check against that day's UniverseSnapshot), so there is nothing to
/// duplicate at this level - a manual run started before UniverseWorker's own manual/scheduled
/// snapshot has landed for the day behaves exactly like a scheduled run would: it logs a
/// warning and does nothing.
///
/// A named run ends only this Worker's own ExecuteAsync task, never
/// IHostApplicationLifetime.StopApplication() - this class is co-hosted alongside six other
/// BackgroundServices in the same Drps.Ingestion process, and a host-wide stop would silently
/// end every sibling's own pending scheduled run too. This is the exact bug the 2026-07-28
/// correction fixes: a live named --run-now=Drps.Regime trigger was confirmed (via log audit)
/// to have killed this Worker's and UniverseWorker's pending scheduled runs mid-wait.
/// </summary>
public class UniverseBarSweepWorker : BackgroundService
{
    // Small buffer past a single day's pull: covers a normal weekend/holiday gap so one
    // missed night's run is recovered automatically on the next run. Duplicate re-ingestion
    // of an already-covered date is safe (append-only, and every downstream consumer already
    // dedups by most-recently-ingested row per date - same precedent as
    // DmaComputationService/DmaVerificationJoinService).
    private const int LookbackDays = 5;

    private static readonly TimeSpan RunTime = NextRunTimeCalculator.DailyRunTime + TimeSpan.FromMinutes(10);

    // Matches UniverseBarSweepRunner's own private WorkerName constant.
    private const string WorkerName = "Drps.UniverseBarSweep";

    private readonly ILogger<UniverseBarSweepWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<DateTime> _nowProvider;
    private readonly bool _runNowNamed;

    public UniverseBarSweepWorker(
        ILogger<UniverseBarSweepWorker> logger,
        IServiceScopeFactory scopeFactory,
        Func<DateTime>? nowProvider = null,
        string[]? args = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _nowProvider = nowProvider ?? (() => DateTime.Now);
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
            var manualSnapshotDate = DateOnly.FromDateTime(_nowProvider());
            await RunSweepCycleAsync(manualSnapshotDate, isManual: true, runNumber: 0, stoppingToken);

            _logger.LogInformation(
                "[UNIVERSE-BAR-SWEEP-WORKER]: named on-demand run for {WorkerName} complete - this Worker's " +
                "execution ends here; other co-hosted workers in this process are unaffected and continue on " +
                "their own schedules",
                WorkerName);
            return;
        }

        // Ends only via stoppingToken cancellation (Ctrl+C, host shutdown) - never on its
        // own, same as Worker's/ExDividendWorker's/SectorWorker's/UniverseWorker's loops.
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _nowProvider();
            var nextRunAt = NextRunTimeCalculator.GetNextRunTime(now, RunTime);
            var delay = nextRunAt - now;

            _logger.LogInformation(
                "[UNIVERSE-BAR-SWEEP-WORKER]: next scheduled run at {NextRunAt} (in {Delay})", nextRunAt, delay);

            await Task.Delay(delay, stoppingToken);

            runNumber++;
            var snapshotDate = DateOnly.FromDateTime(_nowProvider());

            await RunSweepCycleAsync(snapshotDate, isManual: false, runNumber, stoppingToken);
        }
    }

    // Shared by both the named on-demand trigger and the scheduled while loop above - only the
    // log wording branches on isManual, same convention as every other Worker in this codebase.
    private async Task RunSweepCycleAsync(DateOnly snapshotDate, bool isManual, int runNumber, CancellationToken stoppingToken)
    {
        if (isManual)
        {
            _logger.LogInformation("[UNIVERSE-BAR-SWEEP-WORKER]: starting manual/on-demand run for {SnapshotDate}", snapshotDate);
        }
        else
        {
            _logger.LogInformation("[UNIVERSE-BAR-SWEEP-WORKER]: starting scheduled run {RunNumber}", runNumber);
        }

        // end = "yesterday" relative to this run - the most recent trading day whose
        // close print is actually final by 03:10 the following calendar day, same
        // precedent as AlpacaFeeder's own callers elsewhere in this codebase.
        var end = snapshotDate.AddDays(-1);
        var start = end.AddDays(-LookbackDays);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<UniverseBarSweepRunner>();
            await runner.RunAsync(snapshotDate, start, end, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A real shutdown request, not a sweep failure - must propagate so the host
            // actually stops, not get logged-and-continued like the catch below.
            throw;
        }
        catch (Exception ex)
        {
            // This run's failure must not crash the loop or block future days - logged,
            // then back to computing the next 03:10 target.
            _logger.LogError(ex, "[UNIVERSE-BAR-SWEEP-WORKER]: sweep run failed for {SnapshotDate}", snapshotDate);
        }

        if (isManual)
        {
            _logger.LogInformation(
                "[UNIVERSE-BAR-SWEEP-WORKER]: manual/on-demand run for {SnapshotDate} complete", snapshotDate);
        }
        else
        {
            _logger.LogInformation(
                "[UNIVERSE-BAR-SWEEP-WORKER]: scheduled run {RunNumber} complete for {SnapshotDate}", runNumber, snapshotDate);
        }
    }
}
