using Drps.Gate.Scoring;
using Drps.Ingestion.Orchestration;
using Drps.Ingestion.Persistence;
using Drps.Shared.Scheduling;

namespace Drps.Gate;

/// <summary>
/// Scheduling audit (2026-07-19): this used to be a flat 15-minute Task.Delay loop, running
/// 24/7 regardless of trading days, re-scanning against daily-resolution GateScore inputs that
/// only change once per trading day. Replaced with the same fixed-daily-time pattern already
/// proven by Drps.Ingestion's ExDividendWorker/SectorWorker (NextRunTimeCalculator) - not a new
/// scheduler. Runs at 03:35, a 15-minute buffer after Drps.Calculator's fixed 03:20 start (the
/// last of three staggered stages: Ingestion 03:00 -> Calculator 03:20 -> Gate 03:35).
///
/// KNOWN LIMITATION, not a bug to fix here: this stagger is a time-offset guarantee, not a
/// completion guarantee. GateScanService itself makes no external HTTP calls (local DB reads
/// only against CalculatorDbContext/DrpsDbContext), so its own run is structurally fast, but
/// this Worker still never checks that Calculator's 03:20 run actually finished before firing.
/// If Calculator ever runs anomalously long, Gate could still scan against stale/incomplete
/// indicator data. An explicit cross-process completion check is the more robust future
/// alternative if staggered timing ever proves flaky in practice - not built here.
///
/// On-demand manual trigger (CLAUDE.md's "Execution Layer: On-Demand Manual Trigger for
/// Ingestion/Calculator/Gate Workers", 2026-07-24), same mechanism/convention as
/// Drps.Ingestion's and Drps.Calculator's Workers: a "--run-now" startup argument, checked once
/// at construction, runs the exact same guard/scan/record sequence the scheduled path uses (via
/// the shared RunGateCycleAsync below) immediately, before the scheduled while loop is ever
/// entered - no second, competing implementation of that sequence. Gate's own success-gated
/// RecordSuccessfulRunAsync write (only when the scan actually completes without an unhandled
/// exception) is preserved unchanged for both the manual and scheduled path - not made
/// unconditional like Drps.Ingestion's/Drps.Calculator's per-item-isolated Workers. Default (no
/// flag) behavior is unchanged: the scheduled loop's own log text/timing is untouched by this
/// addition.
///
/// Worker-name targeting and clean exit (CLAUDE.md's "On-Demand Worker Targeting: --run-now
/// Worker-Name Filter + Clean Exit", 2026-07-27) - same convention as Drps.Ingestion's/
/// Drps.Calculator's Workers: bare "--run-now" preserves the behavior above byte-for-byte
/// (host stays alive afterward); "--run-now=Drps.Gate" reaches the identical manual run but
/// additionally calls IHostApplicationLifetime.StopApplication() once it completes, so the
/// process exits cleanly.
///
/// Deliberately NOT corrected by the 2026-07-28 "Named On-Demand Worker Targeting: Host-Wide
/// StopApplication() Corrected to Per-Worker Exit" fix, which removed this same call from
/// Drps.Ingestion's seven co-hosted BackgroundServices. This class is the ONLY BackgroundService
/// hosted in the Drps.Gate process - there is no sibling for a host-wide stop to silently kill,
/// so StopApplication() here is genuinely the "one-shot audit tool: run once, exit the whole
/// process" case the correction's own CLAUDE.md addendum calls out as a separate, still-
/// legitimate scenario. If a second BackgroundService is ever added to this process, this call
/// must be revisited using the same reasoning applied to Drps.Ingestion.
/// </summary>
public class Worker : BackgroundService
{
    // 15-minute buffer after Drps.Calculator's fixed 03:20 - see this class's own doc comment.
    private static readonly TimeSpan ScheduledRunTime = new(3, 35, 0);

    // Identifies this Worker's rows in WorkerRunRecord - see WorkerRunGuard's own doc comment.
    // Also the exact string --run-now=<WorkerName> must match for this Worker's named-target
    // path (RunNowArgs.IsNamedRunNow) - single source of truth for "what this worker is called."
    private const string WorkerName = "Drps.Gate";

    private readonly ILogger<Worker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<DateTime> _nowProvider;
    private readonly IHostApplicationLifetime? _hostLifetime;
    private readonly bool _runNowBare;
    private readonly bool _runNowNamed;

    public Worker(
        ILogger<Worker> logger,
        IServiceScopeFactory scopeFactory,
        Func<DateTime>? nowProvider = null,
        IHostApplicationLifetime? hostLifetime = null,
        string[]? args = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        // Clock-injection seam, matching the ExDividendWorker precedent in Drps.Ingestion -
        // lets Drps.Monolith later replay this loop's logic against a historical "as of" date.
        // Live runs use the default real-clock provider.
        _nowProvider = nowProvider ?? (() => DateTime.Now);
        // DI-resolvable singleton, used only by the named-target path below to stop the host
        // after an on-demand run. Optional for the same reason nowProvider/args are optional -
        // this project's direct `new Worker(...)` test construction never supplies it.
        _hostLifetime = hostLifetime;
        // Same optional-DI-seam shape as nowProvider above, and same convention as
        // Drps.Ingestion's/Drps.Calculator's Workers: Program.cs registers the real process args
        // as a singleton so DI-based construction (AddHostedService<Worker>()) resolves the real
        // command line; nothing registers string[] in this project's tests, so direct
        // `new Worker(...)` construction there is unaffected and defaults to no flag.
        _runNowBare = RunNowArgs.IsBareRunNow(args);
        _runNowNamed = RunNowArgs.IsNamedRunNow(args, WorkerName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runNumber = 0;

        // Manual/on-demand trigger - runs once, before the scheduled loop is ever entered.
        // Reuses the exact same guard/scan/record sequence the scheduled path uses via
        // RunGateCycleAsync - no duplicated logic, only the log wording differs
        // (isManual: true), so it's clearly distinguishable from a scheduled run in the logs.
        if (_runNowBare || _runNowNamed)
        {
            var manualAsOf = _nowProvider();
            var manualRunDate = DateOnly.FromDateTime(manualAsOf);
            await RunGateCycleAsync(manualAsOf, manualRunDate, isManual: true, runNumber: 0, stoppingToken);

            // Named-target path only - bare --run-now falls through into the scheduled loop
            // below unchanged. A named run stops the host here, explicitly, rather than relying
            // on stoppingToken cancellation eventually reaching the while loop's check below.
            if (_runNowNamed)
            {
                _logger.LogInformation(
                    "[GATE-WORKER]: named on-demand run for {WorkerName} complete - stopping host", WorkerName);
                _hostLifetime?.StopApplication();
                return;
            }
        }

        // Ends only via stoppingToken cancellation (Ctrl+C, host shutdown) - never on its own,
        // same as every other Worker in this codebase.
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _nowProvider();
            var nextRunAt = NextRunTimeCalculator.GetNextRunTime(now, ScheduledRunTime);
            var delay = nextRunAt - now;

            _logger.LogInformation(
                "[GATE-WORKER]: next scheduled run at {NextRunAt} (in {Delay})", nextRunAt, delay);

            await Task.Delay(delay, stoppingToken);

            runNumber++;
            var asOf = _nowProvider();
            var runDate = DateOnly.FromDateTime(asOf);

            await RunGateCycleAsync(asOf, runDate, isManual: false, runNumber, stoppingToken);
        }
    }

    // Shared by both the manual (--run-now) trigger and the scheduled while loop above - the
    // exact same idempotency guard, GateScanService.RunScanAsync call, and success-gated
    // WorkerRunGuard.RecordSuccessfulRunAsync write, so there is exactly one implementation of
    // "what a run actually does," not a scheduled copy and a manual copy. Only the log wording
    // branches on isManual (runNumber is meaningless for a manual run and is ignored in that
    // branch) - every other line, including the success-gated (not unconditional) record write
    // and exception handling, is identical regardless of how the cycle was triggered. The
    // scheduled-path (isManual: false) log text below is left byte-for-byte identical to what
    // this method's callers previously ran inline.
    private async Task RunGateCycleAsync(DateTime asOf, DateOnly runDate, bool isManual, int runNumber, CancellationToken stoppingToken)
    {
        // Idempotency guard (this session's scheduling-resilience audit, Fix 3) - see
        // WorkerRunGuard's own doc comment for the full mechanism/rationale. Same
        // resolve-inside-try/fail-open shape as Drps.Ingestion's/Drps.Calculator's Workers.
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
                    "[GATE-WORKER]: idempotency guard check failed for {RunDate} - proceeding as if not yet run today (fail-open)",
                    runDate);
            }
        }

        if (alreadyRanToday)
        {
            if (isManual)
            {
                _logger.LogInformation(
                    "[GATE-WORKER]: manual/on-demand run requested for {RunDate}, but already ran successfully today - " +
                    "skipping (same idempotency guard a scheduled run would hit)",
                    runDate);
            }
            else
            {
                _logger.LogInformation(
                    "[GATE-WORKER]: already ran successfully for {RunDate} - skipping scheduled run {RunNumber} " +
                    "(idempotency guard against a same-day re-fire, e.g. a backward clock correction)",
                    runDate, runNumber);
            }

            return;
        }

        if (isManual)
        {
            _logger.LogInformation(
                "[GATE-WORKER]: starting manual/on-demand run for {RunDate} as of {AsOf}", runDate, asOf);
        }
        else
        {
            _logger.LogInformation(
                "[GATE-WORKER]: starting scheduled run {RunNumber} as of {AsOf}", runNumber, asOf);
        }

        // Fresh DI scope per scan (Anti-Spaghetti flush rule) - Gate's natural unit of
        // work is "one scan" rather than "one symbol" (unlike Drps.Calculator's Worker),
        // since candidate tickers are discovered inside the scan itself, not enumerated
        // by Worker from a watchlist. GateScanService is fully DI-resolvable (no clock
        // dependency to thread through by hand) - Worker's own _nowProvider is passed
        // directly into the RunScanAsync call instead.
        var scanSucceeded = false;
        using (var scope = _scopeFactory.CreateScope())
        {
            try
            {
                var scanService = scope.ServiceProvider.GetRequiredService<GateScanService>();
                await scanService.RunScanAsync(asOf, stoppingToken);
                scanSucceeded = true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (isManual)
                {
                    _logger.LogError(ex, "[GATE-WORKER]: manual/on-demand run failed");
                }
                else
                {
                    _logger.LogError(ex, "[GATE-WORKER]: scheduled run {RunNumber} failed", runNumber);
                }
            }
        }

        // Recorded only when the scan actually completed without an unhandled exception -
        // unlike Drps.Ingestion's/Drps.Calculator's per-symbol isolation (where the whole
        // cycle "completing" is the right bar for success regardless of individual item
        // failures), a whole-scan failure here means the pipeline genuinely didn't produce
        // today's output, so a later same-day retry (e.g. a manual restart) must still be
        // permitted rather than being skipped as "already ran." Unchanged for the manual
        // trigger - a failed manual scan must not be recorded as a success either.
        if (scanSucceeded)
        {
            using var guardScope = _scopeFactory.CreateScope();
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
                    "[GATE-WORKER]: failed to record successful run for {RunDate} - the idempotency guard will not " +
                    "recognize today's run as already complete if the Worker restarts later today",
                    runDate);
            }
        }

        if (isManual)
        {
            _logger.LogInformation("[GATE-WORKER]: manual/on-demand run for {RunDate} complete", runDate);
        }
        else
        {
            _logger.LogInformation("[GATE-WORKER]: scheduled run {RunNumber} complete", runNumber);
        }
    }
}
