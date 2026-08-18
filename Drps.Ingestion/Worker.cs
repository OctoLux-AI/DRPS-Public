using Drps.Ingestion.Orchestration;
using Drps.Ingestion.Persistence;
using Drps.Shared;
using Drps.Shared.Scheduling;
using Microsoft.Extensions.Options;

namespace Drps.Ingestion;

/// <summary>
/// Scheduling audit (2026-07-19): this used to be a flat 15-minute Task.Delay loop, running
/// 24/7 regardless of trading days or market hours, against daily-resolution data that only
/// changes once per trading day - burning roughly 96x more Tiingo API calls than necessary
/// (~3,840/day at the old cadence vs. 40/day now) for zero benefit, since Alpaca's own free-tier
/// 200 req/min limit was never remotely close to being threatened at either cadence. Replaced
/// with the same fixed-daily-time pattern already proven by ExDividendWorker/SectorWorker
/// (NextRunTimeCalculator) - not a new scheduler, the identical mechanism, now run at 03:00 (the
/// first of three staggered stages: Ingestion 03:00 -> Calculator 03:20 -> Gate 03:35).
///
/// KNOWN LIMITATION, not a bug to fix here: this stagger is a time-offset guarantee between
/// stages, not a completion guarantee. If a full 40-symbol watchlist pass ever takes
/// anomalously long, Calculator could still start against a stale/incomplete Ingestion pass. An
/// explicit cross-process completion check is the more robust future alternative if staggered
/// timing ever proves flaky in practice - not built here.
///
/// On-demand manual trigger (CLAUDE.md's "Execution Layer: On-Demand Manual Trigger for
/// Ingestion/Calculator/Gate Workers", 2026-07-24): a "--run-now" startup argument, checked
/// once at construction, runs the exact same guard/job/record sequence the scheduled path uses
/// (via the shared RunIngestionCycleAsync below) immediately, before the scheduled while loop
/// is ever entered - no second, competing implementation of that sequence. Default (no flag)
/// behavior is unchanged: the scheduled loop's own log text/timing is untouched by this
/// addition.
///
/// Worker-name targeting (CLAUDE.md's "On-Demand Worker Targeting: --run-now Worker-Name
/// Filter + Clean Exit", 2026-07-27, corrected 2026-07-28 - see "Named On-Demand Worker
/// Targeting: Host-Wide StopApplication() Corrected to Per-Worker Exit" addendum): bare
/// "--run-now" (parsed via RunNowArgs.IsBareRunNow) preserves the original behavior
/// byte-for-byte - it is this Worker's own, original trigger, and the host stays alive
/// afterward exactly as it always has. "--run-now=Drps.Ingestion" (parsed via
/// RunNowArgs.IsNamedRunNow, matched against this class's own WorkerName) is a second,
/// independent way to reach the identical manual run, but ends only THIS Worker's own
/// ExecuteAsync task once it completes - it deliberately does NOT call
/// IHostApplicationLifetime.StopApplication(). This class is one of seven BackgroundServices
/// co-hosted in the same Drps.Ingestion process (alongside ExDividendWorker/SectorWorker/
/// UniverseWorker/UniverseBarSweepWorker/WeeklyVarianceAuditWorker/RegimeWorker) - a host-wide
/// stop from any one of them silently tears down every sibling's own pending scheduled run,
/// which is the exact bug this correction fixes (confirmed live, 2026-07-27: a named
/// --run-now=Drps.Regime trigger killed UniverseWorker/UniverseBarSweepWorker mid-wait for
/// their own next scheduled times). A named value that does not match WorkerName never
/// triggers anything here (this class simply falls through to its normal scheduled loop, as
/// if no flag had been passed).
/// </summary>
public class Worker : BackgroundService
{
    // Identifies this Worker's rows in WorkerRunRecord - see WorkerRunGuard's own doc comment.
    // Also the exact string --run-now=<WorkerName> must match for this Worker's named-target
    // path (RunNowArgs.IsNamedRunNow) - single source of truth for "what this worker is called."
    private const string WorkerName = "Drps.Ingestion";

    private readonly ILogger<Worker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IngestionSettings _settings;
    private readonly WatchlistOptions _watchlist;
    private readonly Func<DateTime> _nowProvider;
    private readonly bool _runNowBare;
    private readonly bool _runNowNamed;

    public Worker(
        ILogger<Worker> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<IngestionSettings> settings,
        IOptions<WatchlistOptions> watchlist,
        Func<DateTime>? nowProvider = null,
        string[]? args = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _watchlist = watchlist.Value;
        // Clock-injection seam, matching ExDividendWorker/SectorWorker's precedent - lets a
        // future replay/test drive this loop against a historical "now" instead of the real
        // clock. Live runs use the default real-clock provider.
        _nowProvider = nowProvider ?? (() => DateTime.Now);
        // Same optional-DI-seam shape as nowProvider above: Program.cs registers the real
        // process args as a singleton so DI-based construction (AddHostedService<Worker>())
        // resolves the real command line; nothing registers string[] in this project's tests,
        // so direct `new Worker(...)` construction there is unaffected and defaults to no flag.
        _runNowBare = RunNowArgs.IsBareRunNow(args);
        _runNowNamed = RunNowArgs.IsNamedRunNow(args, WorkerName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runNumber = 0;

        // Manual/on-demand trigger - runs once, before the scheduled loop is ever entered.
        // Reuses the exact same guard/job/record sequence the scheduled path uses via
        // RunIngestionCycleAsync - no duplicated logic, only the log wording differs
        // (isManual: true), so it's clearly distinguishable from a scheduled run in the logs.
        if (_runNowBare || _runNowNamed)
        {
            var manualRunDate = DateOnly.FromDateTime(_nowProvider());
            await RunIngestionCycleAsync(manualRunDate, isManual: true, runNumber: 0, stoppingToken);

            // Named-target path only - bare --run-now falls through into the scheduled loop
            // below unchanged, exactly as it always has. A named run ends only this Worker's
            // own ExecuteAsync task here, explicitly, rather than relying on stoppingToken
            // cancellation to eventually reach the while loop's check below - returning
            // immediately is what actually guarantees this Worker never re-enters the scheduled
            // loop after a named on-demand run. Deliberately does NOT call
            // IHostApplicationLifetime.StopApplication() - this Worker is co-hosted alongside
            // six other BackgroundServices in the same Drps.Ingestion process, and a host-wide
            // stop would silently end every one of their own pending scheduled runs too (see
            // this class's own doc comment).
            if (_runNowNamed)
            {
                _logger.LogInformation(
                    "[WORKER]: named on-demand run for {WorkerName} complete - this Worker's execution ends here; " +
                    "other co-hosted workers in this process are unaffected and continue on their own schedules",
                    WorkerName);
                return;
            }
        }

        // Ends only via stoppingToken cancellation (Ctrl+C, host shutdown) - never on its own,
        // same as every other Worker in this codebase.
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _nowProvider();
            var nextRunAt = NextRunTimeCalculator.GetNextRunTime(now);
            var delay = nextRunAt - now;

            _logger.LogInformation(
                "[WORKER]: next scheduled run at {NextRunAt} (in {Delay})", nextRunAt, delay);

            await Task.Delay(delay, stoppingToken);

            runNumber++;
            var runDate = DateOnly.FromDateTime(_nowProvider());

            await RunIngestionCycleAsync(runDate, isManual: false, runNumber, stoppingToken);
        }
    }

    // Shared by both the manual (--run-now) trigger and the scheduled while loop above - the
    // exact same idempotency guard, per-symbol IngestionJob.RunOnceAsync calls, and
    // WorkerRunGuard.RecordSuccessfulRunAsync write, so there is exactly one implementation of
    // "what a run actually does," not a scheduled copy and a manual copy. Only the log wording
    // branches on isManual (runNumber is meaningless for a manual run and is ignored in that
    // branch) - every other line, including exception handling, is identical regardless of how
    // the cycle was triggered. The scheduled-path (isManual: false) log text below is left
    // byte-for-byte identical to what this method's callers previously ran inline.
    private async Task RunIngestionCycleAsync(DateOnly runDate, bool isManual, int runNumber, CancellationToken stoppingToken)
    {
        // Idempotency guard (this session's scheduling-resilience audit, Fix 3) - see
        // WorkerRunGuard's own doc comment for the full mechanism/rationale. DrpsDbContext
        // is resolved INSIDE this try block (not before it), matching this session's own
        // earlier exception-boundary fix for Drps.Gate's Worker - a resolution failure here
        // must be caught and logged, not crash the loop. Fails OPEN on any error (treats it
        // as "not yet run today" and proceeds) rather than letting a transient DB hiccup
        // block a legitimate scheduled run - the guard's own failure must never become a
        // new reason the pipeline doesn't run.
        var alreadyRanToday = false;
        using (var guardScope = _scopeFactory.CreateScope())
        {
            try
            {
                var guardDbContext = guardScope.ServiceProvider.GetRequiredService<DrpsDbContext>();
                alreadyRanToday = await WorkerRunGuard.HasRunTodayAsync(guardDbContext, WorkerName, runDate, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[WORKER]: idempotency guard check failed for {RunDate} - proceeding as if not yet run today (fail-open)",
                    runDate);
            }
        }

        if (alreadyRanToday)
        {
            if (isManual)
            {
                _logger.LogInformation(
                    "[WORKER]: manual/on-demand run requested for {RunDate}, but already ran successfully today - " +
                    "skipping (same idempotency guard a scheduled run would hit)",
                    runDate);
            }
            else
            {
                _logger.LogInformation(
                    "[WORKER]: already ran successfully for {RunDate} - skipping scheduled run {RunNumber} " +
                    "(idempotency guard against a same-day re-fire, e.g. a backward clock correction)",
                    runDate, runNumber);
            }

            return;
        }

        if (isManual)
        {
            _logger.LogInformation("[WORKER]: starting manual/on-demand run for {RunDate}", runDate);
        }
        else
        {
            _logger.LogInformation("[WORKER]: starting scheduled run {RunNumber}", runNumber);
        }

        DateOnly start, end;
        if (_settings.StartDate.HasValue && _settings.EndDate.HasValue)
        {
            start = _settings.StartDate.Value;
            end = _settings.EndDate.Value;
            _logger.LogInformation(
                "[WORKER]: using explicit date range override {Start} to {End}", start, end);
        }
        else
        {
            end = DateOnly.FromDateTime(_nowProvider());
            start = end.AddDays(-_settings.LookbackDays);
            _logger.LogInformation(
                "[WORKER]: using LookbackDays-computed range {Start} to {End} ({LookbackDays} day(s))",
                start, end, _settings.LookbackDays);
        }

        var processedCount = 0;

        foreach (var symbol in _watchlist.Symbols)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var ingestionJob = scope.ServiceProvider.GetRequiredService<IngestionJob>();
                await ingestionJob.RunOnceAsync(symbol, start, end, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // A real shutdown request, not a data-fetching failure - must propagate so
                // the host actually stops, not get logged-and-continued like the catch below.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WORKER]: ingestion job failed for {Symbol}", symbol);
            }

            processedCount++;
        }

        if (isManual)
        {
            _logger.LogInformation(
                "[WORKER]: manual/on-demand run for {RunDate} complete, processed {ProcessedCount} symbol(s)",
                runDate, processedCount);
        }
        else
        {
            _logger.LogInformation(
                "[WORKER]: scheduled run {RunNumber} complete, processed {ProcessedCount} symbol(s)",
                runNumber, processedCount);
        }

        // Recorded only once the full per-symbol pass has completed (regardless of
        // individual symbol failures, already isolated/logged above - same "the cycle
        // reached its end" interpretation of "ran successfully" the rest of this codebase
        // uses for per-item isolation). Never reached if the loop was cut short by a real
        // cancellation. Fails open (logs, does not throw) - a failure to record success
        // only means a future same-day restart won't be recognized as a re-fire; it does
        // not affect today's already-completed work.
        using (var guardScope = _scopeFactory.CreateScope())
        {
            try
            {
                var guardDbContext = guardScope.ServiceProvider.GetRequiredService<DrpsDbContext>();
                await WorkerRunGuard.RecordSuccessfulRunAsync(guardDbContext, WorkerName, runDate, _nowProvider(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[WORKER]: failed to record successful run for {RunDate} - the idempotency guard will not " +
                    "recognize today's run as already complete if the Worker restarts later today",
                    runDate);
            }
        }
    }
}
