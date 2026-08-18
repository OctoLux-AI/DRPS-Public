using Drps.Ingestion.Orchestration;
using Drps.Shared.Scheduling;

namespace Drps.Ingestion;

/// <summary>
/// Separate, independent daily loop for universe-snapshot ingestion - same shape precedent as
/// ExDividendWorker/SectorWorker (once-daily at 03:00, not folded into Worker's intraday
/// loop), except there is no per-ticker loop here: a universe refresh is a single global
/// operation covering the whole NasdaqTrader symbol directory, not a per-watchlist-symbol
/// fetch, so ExecuteAsync calls <see cref="UniverseIngestionRunner"/> once per scheduled run
/// rather than once per symbol.
///
/// On-demand worker-name targeting (CLAUDE.md's "On-Demand Worker Targeting: --run-now
/// Worker-Name Filter + Clean Exit", 2026-07-27, corrected 2026-07-28 - see "Named On-Demand
/// Worker Targeting: Host-Wide StopApplication() Corrected to Per-Worker Exit" addendum): this
/// class has no bare "--run-now" path, same as RegimeWorker - only
/// "--run-now=Drps.UniverseSnapshot" (matched against WorkerName below, the same identifier
/// UniverseIngestionRunner already uses for its own WorkerRunRecord rows) triggers a manual
/// run. UniverseIngestionRunner owns its own idempotency guard internally, so there is nothing
/// to duplicate at this level.
///
/// A named run ends only this Worker's own ExecuteAsync task, never
/// IHostApplicationLifetime.StopApplication() - this class is co-hosted alongside six other
/// BackgroundServices in the same Drps.Ingestion process, and a host-wide stop would silently
/// end every sibling's own pending scheduled run too. This is the exact bug the 2026-07-28
/// correction fixes: a live named --run-now=Drps.Regime trigger was confirmed (via log audit)
/// to have killed this Worker's and UniverseBarSweepWorker's pending scheduled runs mid-wait.
/// </summary>
public class UniverseWorker : BackgroundService
{
    // Matches UniverseIngestionRunner's own private WorkerName constant.
    private const string WorkerName = "Drps.UniverseSnapshot";

    private readonly ILogger<UniverseWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<DateTime> _nowProvider;
    private readonly bool _runNowNamed;

    public UniverseWorker(
        ILogger<UniverseWorker> logger,
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
            await RunUniverseCycleAsync(manualSnapshotDate, isManual: true, runNumber: 0, stoppingToken);

            _logger.LogInformation(
                "[UNIVERSE-WORKER]: named on-demand run for {WorkerName} complete - this Worker's execution ends here; " +
                "other co-hosted workers in this process are unaffected and continue on their own schedules",
                WorkerName);
            return;
        }

        // Ends only via stoppingToken cancellation (Ctrl+C, host shutdown) - never on its
        // own, same as Worker's/ExDividendWorker's/SectorWorker's loops.
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _nowProvider();
            var nextRunAt = NextRunTimeCalculator.GetNextRunTime(now);
            var delay = nextRunAt - now;

            _logger.LogInformation(
                "[UNIVERSE-WORKER]: next scheduled run at {NextRunAt} (in {Delay})", nextRunAt, delay);

            await Task.Delay(delay, stoppingToken);

            runNumber++;
            var snapshotDate = DateOnly.FromDateTime(_nowProvider());

            await RunUniverseCycleAsync(snapshotDate, isManual: false, runNumber, stoppingToken);
        }
    }

    // Shared by both the named on-demand trigger and the scheduled while loop above - only the
    // log wording branches on isManual, same convention as every other Worker in this codebase.
    private async Task RunUniverseCycleAsync(DateOnly snapshotDate, bool isManual, int runNumber, CancellationToken stoppingToken)
    {
        if (isManual)
        {
            _logger.LogInformation("[UNIVERSE-WORKER]: starting manual/on-demand run for {SnapshotDate}", snapshotDate);
        }
        else
        {
            _logger.LogInformation("[UNIVERSE-WORKER]: starting scheduled run {RunNumber}", runNumber);
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<UniverseIngestionRunner>();
            await runner.RunAsync(snapshotDate, stoppingToken);
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
            _logger.LogError(ex, "[UNIVERSE-WORKER]: universe snapshot run failed for {SnapshotDate}", snapshotDate);
        }

        if (isManual)
        {
            _logger.LogInformation(
                "[UNIVERSE-WORKER]: manual/on-demand run for {SnapshotDate} complete", snapshotDate);
        }
        else
        {
            _logger.LogInformation(
                "[UNIVERSE-WORKER]: scheduled run {RunNumber} complete for {SnapshotDate}", runNumber, snapshotDate);
        }
    }
}
