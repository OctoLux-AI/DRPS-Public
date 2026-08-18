using Drps.Ingestion.Orchestration;
using Drps.Ingestion.Persistence;
using Drps.Shared.Scheduling;

namespace Drps.Ingestion;

/// <summary>
/// Weekly (not nightly) loop for the Alpaca-vs-Tiingo data-quality audit (CLAUDE.md, "Weekly
/// Data-Quality Audit: Alpaca vs. Tiingo Variance", 2026-07-22) - deliberately its own cadence,
/// not folded into Worker's daily 03:00 loop. Fires Saturday 04:00 local time: markets are
/// closed, and both Alpaca's and Tiingo's Friday prints have had a full night plus the
/// weekend-worth of margin to land before this runs. WeeklyVarianceAuditService.RunAsync is
/// then given "this week's Friday" (the most recent Friday on/before the actual run date) as
/// the week-ending anchor, rather than assuming today literally is Saturday - the same
/// self-correcting instinct as Worker's own StartDate/EndDate override, in case a delayed
/// restart ever causes this to fire on some other day.
///
/// On-demand worker-name targeting (CLAUDE.md's "On-Demand Worker Targeting: --run-now
/// Worker-Name Filter + Clean Exit", 2026-07-27, corrected 2026-07-28 - see "Named On-Demand
/// Worker Targeting: Host-Wide StopApplication() Corrected to Per-Worker Exit" addendum): this
/// class has no bare "--run-now" path, same as RegimeWorker - only
/// "--run-now=Drps.WeeklyVarianceAudit" (matched against WorkerName below) triggers a manual
/// run, reusing the exact same guard/service/record sequence the scheduled path uses via
/// RunAuditCycleAsync. The manual run's week-ending anchor is computed the same way the
/// scheduled path's is - "this week's Friday" relative to whatever _nowProvider() returns at
/// the moment the manual run fires, not necessarily an actual Saturday.
///
/// A named run ends only this Worker's own ExecuteAsync task, never
/// IHostApplicationLifetime.StopApplication() - this class is co-hosted alongside six other
/// BackgroundServices in the same Drps.Ingestion process, and a host-wide stop would silently
/// end every sibling's own pending scheduled run too (the confirmed 2026-07-27 bug this
/// correction fixes).
/// </summary>
public class WeeklyVarianceAuditWorker : BackgroundService
{
    private static readonly TimeSpan ScheduledRunTime = new(4, 0, 0);
    private const DayOfWeek ScheduledDayOfWeek = DayOfWeek.Saturday;

    // Identifies this Worker's rows in WorkerRunRecord - see WorkerRunGuard's own doc comment.
    // Reused as-is for a weekly cadence: the guard only ever asks "has (WorkerName, RunDate)
    // already recorded a successful run," so keying RunDate on the week-ending date instead of
    // a daily date makes it a once-per-week guard with no changes to WorkerRunGuard itself.
    // Also the exact string --run-now=<WorkerName> must match for this Worker's named-target
    // path (RunNowArgs.IsNamedRunNow).
    private const string WorkerName = "Drps.WeeklyVarianceAudit";

    private readonly ILogger<WeeklyVarianceAuditWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<DateTime> _nowProvider;
    private readonly bool _runNowNamed;

    public WeeklyVarianceAuditWorker(
        ILogger<WeeklyVarianceAuditWorker> logger,
        IServiceScopeFactory scopeFactory,
        Func<DateTime>? nowProvider = null,
        string[]? args = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        // Clock-injection seam, matching every other Worker in this codebase - lets a test
        // drive this loop against a fixed "now" instead of the real clock.
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
            var manualRunDate = DateOnly.FromDateTime(_nowProvider());
            var manualWeekEndingDate = NextRunTimeCalculator.GetMostRecentOccurrence(manualRunDate, DayOfWeek.Friday);
            await RunAuditCycleAsync(manualWeekEndingDate, isManual: true, runNumber: 0, stoppingToken);

            _logger.LogInformation(
                "[WEEKLY-VARIANCE-AUDIT-WORKER]: named on-demand run for {WorkerName} complete - this Worker's " +
                "execution ends here; other co-hosted workers in this process are unaffected and continue on " +
                "their own schedules",
                WorkerName);
            return;
        }

        // Ends only via stoppingToken cancellation (Ctrl+C, host shutdown) - never on its own,
        // same as every other Worker in this codebase.
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _nowProvider();
            var nextRunAt = NextRunTimeCalculator.GetNextWeeklyRunTime(now, ScheduledDayOfWeek, ScheduledRunTime);
            var delay = nextRunAt - now;

            _logger.LogInformation(
                "[WEEKLY-VARIANCE-AUDIT-WORKER]: next scheduled run at {NextRunAt} (in {Delay})", nextRunAt, delay);

            await Task.Delay(delay, stoppingToken);

            runNumber++;
            var runDate = DateOnly.FromDateTime(_nowProvider());
            var weekEndingDate = NextRunTimeCalculator.GetMostRecentOccurrence(runDate, DayOfWeek.Friday);

            await RunAuditCycleAsync(weekEndingDate, isManual: false, runNumber, stoppingToken);
        }
    }

    // Shared by both the named on-demand trigger and the scheduled while loop above - the exact
    // same idempotency guard, WeeklyVarianceAuditService.RunAsync call, and
    // WorkerRunGuard.RecordSuccessfulRunAsync write, so there is exactly one implementation of
    // "what a run actually does." Only the log wording branches on isManual (runNumber is
    // meaningless for a manual run and is ignored in that branch).
    private async Task RunAuditCycleAsync(DateOnly weekEndingDate, bool isManual, int runNumber, CancellationToken stoppingToken)
    {
        // Idempotency guard, same mechanism/rationale as every other Worker's own
        // (WorkerRunGuard's doc comment) - here it guards against re-auditing the same
        // week twice, not the same day twice. Fails open on any error.
        var alreadyRanThisWeek = false;
        using (var guardScope = _scopeFactory.CreateScope())
        {
            try
            {
                var guardDbContext = guardScope.ServiceProvider.GetRequiredService<DrpsDbContext>();
                alreadyRanThisWeek = await WorkerRunGuard.HasRunTodayAsync(guardDbContext, WorkerName, weekEndingDate, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[WEEKLY-VARIANCE-AUDIT-WORKER]: idempotency guard check failed for week ending " +
                    "{WeekEndingDate:yyyy-MM-dd} - proceeding as if not yet run this week (fail-open)",
                    weekEndingDate);
            }
        }

        if (alreadyRanThisWeek)
        {
            if (isManual)
            {
                _logger.LogInformation(
                    "[WEEKLY-VARIANCE-AUDIT-WORKER]: manual/on-demand run requested for week ending " +
                    "{WeekEndingDate:yyyy-MM-dd}, but already ran successfully this week - skipping",
                    weekEndingDate);
            }
            else
            {
                _logger.LogInformation(
                    "[WEEKLY-VARIANCE-AUDIT-WORKER]: already ran successfully for week ending {WeekEndingDate:yyyy-MM-dd} - " +
                    "skipping scheduled run {RunNumber}",
                    weekEndingDate, runNumber);
            }

            return;
        }

        if (isManual)
        {
            _logger.LogInformation(
                "[WEEKLY-VARIANCE-AUDIT-WORKER]: starting manual/on-demand run for week ending {WeekEndingDate:yyyy-MM-dd}",
                weekEndingDate);
        }
        else
        {
            _logger.LogInformation(
                "[WEEKLY-VARIANCE-AUDIT-WORKER]: starting scheduled run {RunNumber} for week ending {WeekEndingDate:yyyy-MM-dd}",
                runNumber, weekEndingDate);
        }

        using (var scope = _scopeFactory.CreateScope())
        {
            try
            {
                var service = scope.ServiceProvider.GetRequiredService<WeeklyVarianceAuditService>();
                await service.RunAsync(weekEndingDate, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[WEEKLY-VARIANCE-AUDIT-WORKER]: audit run failed for week ending {WeekEndingDate:yyyy-MM-dd}", weekEndingDate);
            }
        }

        if (isManual)
        {
            _logger.LogInformation(
                "[WEEKLY-VARIANCE-AUDIT-WORKER]: manual/on-demand run complete for week ending {WeekEndingDate:yyyy-MM-dd}",
                weekEndingDate);
        }
        else
        {
            _logger.LogInformation(
                "[WEEKLY-VARIANCE-AUDIT-WORKER]: scheduled run {RunNumber} complete for week ending {WeekEndingDate:yyyy-MM-dd}",
                runNumber, weekEndingDate);
        }

        using (var guardScope = _scopeFactory.CreateScope())
        {
            try
            {
                var guardDbContext = guardScope.ServiceProvider.GetRequiredService<DrpsDbContext>();
                await WorkerRunGuard.RecordSuccessfulRunAsync(guardDbContext, WorkerName, weekEndingDate, _nowProvider(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[WEEKLY-VARIANCE-AUDIT-WORKER]: failed to record successful run for week ending {WeekEndingDate:yyyy-MM-dd}" +
                    "- the idempotency guard will not recognize this week as already complete if the Worker restarts later this week",
                    weekEndingDate);
            }
        }
    }
}
