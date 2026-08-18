using Drps.Calculator.Atr;
using Drps.Calculator.Dma;
using Drps.Calculator.Dma.RollingDma;
using Drps.Calculator.Rsi;
using Drps.Calculator.Rvol;
using Drps.Calculator.Verification;
using Drps.Ingestion.Orchestration;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Drps.Shared.Scheduling;

namespace Drps.Calculator;

/// <summary>
/// Scheduling audit (2026-07-19): this used to be a flat 15-minute Task.Delay loop, running
/// 24/7 regardless of trading days, against daily-resolution indicators computed from data that
/// only changes once per trading day. Replaced with the same fixed-daily-time pattern already
/// proven by Drps.Ingestion's ExDividendWorker/SectorWorker (NextRunTimeCalculator) - not a new
/// scheduler. Runs at 03:20, a 20-minute buffer after Drps.Ingestion's fixed 03:00 start (the
/// second of three staggered stages: Ingestion 03:00 -> Calculator 03:20 -> Gate 03:35).
///
/// KNOWN LIMITATION, not a bug to fix here: this stagger is a time-offset guarantee, not a
/// completion guarantee - still runs independently on its own schedule, never waits for or
/// calls into Ingestion directly. If Ingestion's own run ever takes anomalously long (its
/// measured duration for a comparable HTTP-call volume is on the order of tens of seconds, not
/// minutes - see the scheduling-audit CLAUDE.md block), Calculator could still start against a
/// stale/incomplete Ingestion pass. An explicit cross-process completion check is the more
/// robust future alternative if staggered timing ever proves flaky in practice - not built here.
///
/// On-demand manual trigger (CLAUDE.md's "Execution Layer: On-Demand Manual Trigger for
/// Ingestion/Calculator/Gate Workers", 2026-07-24), same mechanism/convention as
/// Drps.Ingestion's Worker: a "--run-now" startup argument, checked once at construction, runs
/// the exact same guard/computation/record sequence the scheduled path uses (via the shared
/// RunCalculatorCycleAsync below) immediately, before the scheduled while loop is ever entered -
/// no second, competing implementation of that sequence. Default (no flag) behavior is
/// unchanged: the scheduled loop's own log text/timing is untouched by this addition.
///
/// Worker-name targeting and clean exit (CLAUDE.md's "On-Demand Worker Targeting: --run-now
/// Worker-Name Filter + Clean Exit", 2026-07-27) - same convention as Drps.Ingestion's Worker:
/// bare "--run-now" preserves the behavior above byte-for-byte (host stays alive afterward);
/// "--run-now=Drps.Calculator" reaches the identical manual run but additionally calls
/// IHostApplicationLifetime.StopApplication() once it completes, so the process exits cleanly.
///
/// Deliberately NOT corrected by the 2026-07-28 "Named On-Demand Worker Targeting: Host-Wide
/// StopApplication() Corrected to Per-Worker Exit" fix, which removed this same call from
/// Drps.Ingestion's seven co-hosted BackgroundServices. This class is the ONLY BackgroundService
/// hosted in the Drps.Calculator process - there is no sibling for a host-wide stop to
/// silently kill, so StopApplication() here is genuinely the "one-shot audit tool: run once,
/// exit the whole process" case the correction's own CLAUDE.md addendum calls out as a
/// separate, still-legitimate scenario. If a second BackgroundService is ever added to this
/// process, this call must be revisited using the same reasoning applied to Drps.Ingestion.
/// </summary>
public class Worker : BackgroundService
{
    // 20-minute buffer after Drps.Ingestion's fixed 03:00 - see this class's own doc comment.
    private static readonly TimeSpan ScheduledRunTime = new(3, 20, 0);

    // Identifies this Worker's rows in WorkerRunRecord - see WorkerRunGuard's own doc comment.
    // Also the exact string --run-now=<WorkerName> must match for this Worker's named-target
    // path (RunNowArgs.IsNamedRunNow) - single source of truth for "what this worker is called."
    private const string WorkerName = "Drps.Calculator";

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
        // Clock-injection seam, matching ExDividendWorker/SectorWorker's precedent - lets a
        // future replay/test drive this loop against a historical "now" instead of the real
        // clock. Live runs use the default real-clock provider.
        _nowProvider = nowProvider ?? (() => DateTime.Now);
        // DI-resolvable singleton, used only by the named-target path below to stop the host
        // after an on-demand run. Optional for the same reason nowProvider/args are optional -
        // this project's direct `new Worker(...)` test construction never supplies it.
        _hostLifetime = hostLifetime;
        // Same optional-DI-seam shape as nowProvider above, and same convention as
        // Drps.Ingestion's Worker: Program.cs registers the real process args as a singleton so
        // DI-based construction (AddHostedService<Worker>()) resolves the real command line;
        // nothing registers string[] in this project's tests, so direct `new Worker(...)`
        // construction there is unaffected and defaults to no flag.
        _runNowBare = RunNowArgs.IsBareRunNow(args);
        _runNowNamed = RunNowArgs.IsNamedRunNow(args, WorkerName);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var runNumber = 0;

        // Manual/on-demand trigger - runs once, before the scheduled loop is ever entered.
        // Reuses the exact same guard/computation/record sequence the scheduled path uses via
        // RunCalculatorCycleAsync - no duplicated logic, only the log wording differs
        // (isManual: true), so it's clearly distinguishable from a scheduled run in the logs.
        if (_runNowBare || _runNowNamed)
        {
            var manualRunDate = DateOnly.FromDateTime(_nowProvider());
            await RunCalculatorCycleAsync(manualRunDate, isManual: true, runNumber: 0, stoppingToken);

            // Named-target path only - bare --run-now falls through into the scheduled loop
            // below unchanged. A named run stops the host here, explicitly, rather than relying
            // on stoppingToken cancellation eventually reaching the while loop's check below.
            if (_runNowNamed)
            {
                _logger.LogInformation(
                    "[WORKER]: named on-demand run for {WorkerName} complete - stopping host", WorkerName);
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
                "[WORKER]: next scheduled run at {NextRunAt} (in {Delay})", nextRunAt, delay);

            await Task.Delay(delay, stoppingToken);

            runNumber++;
            var runDate = DateOnly.FromDateTime(_nowProvider());

            await RunCalculatorCycleAsync(runDate, isManual: false, runNumber, stoppingToken);
        }
    }

    // Shared by both the manual (--run-now) trigger and the scheduled while loop above - the
    // exact same idempotency guard, RollingDmaStateService run, per-symbol DMA/RSI/RSI-slope/
    // RSI-concavity/RVOL/ATR computation calls, and WorkerRunGuard.RecordSuccessfulRunAsync
    // write, so there is exactly
    // one implementation of "what a run actually does," not a scheduled copy and a manual copy.
    // Only the log wording branches on isManual (runNumber is meaningless for a manual run and
    // is ignored in that branch) - every other line, including exception handling, is identical
    // regardless of how the cycle was triggered. The scheduled-path (isManual: false) log text
    // below is left byte-for-byte identical to what this method's callers previously ran inline.
    private async Task RunCalculatorCycleAsync(DateOnly runDate, bool isManual, int runNumber, CancellationToken stoppingToken)
    {
        // Idempotency guard (this session's scheduling-resilience audit, Fix 3) - see
        // WorkerRunGuard's own doc comment for the full mechanism/rationale. Same shape as
        // Drps.Ingestion's Worker: DrpsDbContext resolved INSIDE the try block, fails open
        // on any error rather than blocking a legitimate scheduled run.
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

        // Full-universe cheap pre-filter (CLAUDE.md's "Rolling DMA State Machine") - runs
        // once per scheduled run, not once per candidate symbol like the per-symbol loop
        // below, since it reads that night's full UniverseSnapshot internally. Own DI scope
        // and own isolated try/catch, same Anti-Spaghetti flush/isolation discipline as
        // every indicator computation below - a failure here must not block this run's
        // own DMA/RSI/RVOL/ATR computations, or vice versa. The isolation itself
        // (catch, log, continue) is unchanged - only the success/failure OUTCOME is now
        // tracked locally, so it can be reported honestly below instead of the previously
        // hardcoded allSourcesSucceeded: true regardless of whether this actually ran clean.
        var rollingDmaStateMachineSucceeded = true;
        string? rollingDmaFailureDetail = null;
        using (var rollingDmaScope = _scopeFactory.CreateScope())
        {
            try
            {
                var rollingDmaService = rollingDmaScope.ServiceProvider.GetRequiredService<RollingDmaStateService>();
                await rollingDmaService.RunAsync(runDate, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                rollingDmaStateMachineSucceeded = false;
                rollingDmaFailureDetail = $"RollingDmaStateMachine: {ex.Message}";
                _logger.LogError(ex, "[WORKER]: rolling DMA state machine run failed for {RunDate}", runDate);
            }
        }

        // Discovery (CLAUDE.md's "Universe-Wide Dynamic Discovery - Watchlist File Retired
        // from Production Path", 2026-08-05) - replaces the former Watchlist +
        // GetFullyAlignedTickersAsync union entirely. GetDma5AlignedTickersAsync is now the
        // sole compute-scope source: the same DMA-5-only "aligned" definition Gate's own Tier
        // 1 entry gate uses (per the 2026-08-04 DMA blast-radius-scoping relaxation), so
        // Calculator's discovery filter and Gate's own entry gate never diverge. Own DI scope
        // and own isolated try/catch, same Anti-Spaghetti flush/isolation discipline as the
        // rolling DMA state machine call above. Fails open to an empty candidate list on any
        // unexpected failure - never throws, never silently skips logging.
        var candidateTickers = new List<string>();
        using (var discoveryScope = _scopeFactory.CreateScope())
        {
            try
            {
                var discoveryService = discoveryScope.ServiceProvider.GetRequiredService<RollingDmaStateService>();
                candidateTickers = await discoveryService.GetDma5AlignedTickersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[WORKER]: failed to read DMA-5-aligned candidate tickers from RollingDmaStates for " +
                    "{RunDate} - no candidates for this run",
                    runDate);
            }
        }

        if (candidateTickers.Count == 0)
        {
            _logger.LogInformation(
                "[WORKER]: no DMA-5-aligned candidate ticker(s) found for {RunDate} - nothing to verify or " +
                "compute this run",
                runDate);
        }
        else
        {
            _logger.LogInformation(
                "[WORKER]: compute scope for {RunDate}: {TotalCount} DMA-5-aligned candidate ticker(s) - " +
                "verifying before compute",
                runDate, candidateTickers.Count);

            // Dual-source (Alpaca+Tiingo) verification of this run's candidate list
            // (CandidateBarVerificationService) - runs once, before any per-symbol
            // DMA/RSI/RVOL/ATR computation below, so each candidate's BarVerification exists
            // ahead of Gate's own DMA-5 Tier 1 verification check reading it later. Own DI
            // scope and own isolated try/catch, same discipline as every other optional step
            // in this cycle - a verification failure must not block indicator computation for
            // candidates whose bars still verify fine, or whose data is already on hand.
            using var verificationScope = _scopeFactory.CreateScope();
            try
            {
                var verificationService = verificationScope.ServiceProvider.GetRequiredService<CandidateBarVerificationService>();
                await verificationService.VerifyCandidatesAsync(candidateTickers, runDate, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[WORKER]: candidate bar verification failed for {RunDate} - proceeding to indicator " +
                    "computation regardless",
                    runDate);
            }
        }

        var symbolsToProcess = candidateTickers;
        var processedCount = 0;

        // Indicator-write outcome tracking (2026-08-01 audit fix: "AllSourcesSucceeded -
        // Reflect Actual Indicator-Write Success") - counts DMA/RSI/RVOL/ATR writes
        // specifically, the four indicators Gate's Tier 1 gate and composite score actually
        // consume. RsiSlope/RsiConcavity are deliberately excluded (watch-only, unwired to
        // any consumer per CLAUDE.md's "RsiSlope / RsiConcavity: Design Direction Locked" -
        // their own failures don't threaten a real decision the way DMA/RSI/RVOL/ATR's do).
        // "Attempted" is derived as succeeded + failed rather than tracked separately, since
        // each compute call either completes or throws - there is no partial-attempt state
        // to count. This is what closes the false-success gap the 2026-08-01 audit found:
        // before this fix, all 160 of these writes could fail (as they did on 8/1) while the
        // run still recorded AllSourcesSucceeded = true, because that flag was wired only to
        // the rolling DMA state machine's own outcome, never to whether any of these per-
        // symbol writes actually landed.
        var indicatorWritesSucceeded = 0;
        var indicatorWritesFailed = 0;

        foreach (var symbol in symbolsToProcess)
        {
            // Every candidate in this run's compute scope comes from GetDma5AlignedTickersAsync
            // alone (Watchlist retired from this loop per CLAUDE.md's "Universe-Wide Dynamic
            // Discovery" decision, 2026-08-05), so there is exactly one origin value now, not a
            // per-symbol lookup.
            const TickerSourceOrigin sourceOrigin = TickerSourceOrigin.DiscoveredAligned;

            // One scope per symbol (Anti-Spaghetti flush rule), shared by both
            // indicators computed for that symbol - but each indicator gets its own
            // try/catch, so a DMA failure doesn't block RSI for the same symbol (or
            // vice versa), same isolation philosophy as per-symbol isolation itself.
            using var scope = _scopeFactory.CreateScope();

            try
            {
                var dmaService = scope.ServiceProvider.GetRequiredService<DmaComputationService>();
                await dmaService.ComputeAsync(symbol, sourceOrigin, stoppingToken);
                indicatorWritesSucceeded++;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // A real shutdown request, not a computation failure - must propagate so
                // the host actually stops, not get logged-and-continued like the catch below.
                throw;
            }
            catch (Exception ex)
            {
                indicatorWritesFailed++;
                _logger.LogError(ex, "[WORKER]: DMA computation failed for {Symbol}", symbol);
            }

            try
            {
                var rsiService = scope.ServiceProvider.GetRequiredService<RsiComputationService>();
                await rsiService.ComputeAsync(symbol, sourceOrigin, stoppingToken);
                indicatorWritesSucceeded++;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                indicatorWritesFailed++;
                _logger.LogError(ex, "[WORKER]: RSI computation failed for {Symbol}", symbol);
            }

            try
            {
                // Watch-only (CLAUDE.md, "RsiSlope / RsiConcavity: Design Direction Locked",
                // 2026-07-31) - must run after RsiComputationService above, since it reads
                // RsiIndicators rather than raw bars.
                var rsiSlopeService = scope.ServiceProvider.GetRequiredService<RsiSlopeComputationService>();
                await rsiSlopeService.ComputeAsync(symbol, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WORKER]: RSI slope computation failed for {Symbol}", symbol);
            }

            try
            {
                // Must run after RsiSlopeComputationService immediately above, since it reads
                // RsiSlopeIndicators rather than RsiIndicators/raw bars.
                var rsiConcavityService = scope.ServiceProvider.GetRequiredService<RsiConcavityComputationService>();
                await rsiConcavityService.ComputeAsync(symbol, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WORKER]: RSI concavity computation failed for {Symbol}", symbol);
            }

            try
            {
                var rvolService = scope.ServiceProvider.GetRequiredService<RvolComputationService>();
                await rvolService.ComputeAsync(symbol, sourceOrigin, stoppingToken);
                indicatorWritesSucceeded++;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                indicatorWritesFailed++;
                _logger.LogError(ex, "[WORKER]: RVOL computation failed for {Symbol}", symbol);
            }

            try
            {
                var atrService = scope.ServiceProvider.GetRequiredService<AtrComputationService>();
                await atrService.ComputeAsync(symbol, sourceOrigin, stoppingToken);
                indicatorWritesSucceeded++;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                indicatorWritesFailed++;
                _logger.LogError(ex, "[WORKER]: ATR computation failed for {Symbol}", symbol);
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

        // Summary line (2026-08-01 audit fix) - so a human reading the log knows the real
        // indicator-write outcome without counting individual "computation failed" error
        // lines by hand, which is exactly how the 8/1 false-success run was first diagnosed.
        var indicatorWritesAttempted = indicatorWritesSucceeded + indicatorWritesFailed;
        _logger.LogInformation(
            "[WORKER]: compute pass complete for {RunDate}: {SymbolCount} symbol(s), {AttemptedCount} indicator " +
            "write(s) attempted (DMA/RSI/RVOL/ATR), {SucceededCount} succeeded, {FailedCount} failed",
            runDate, symbolsToProcess.Count, indicatorWritesAttempted, indicatorWritesSucceeded, indicatorWritesFailed);

        // AllSourcesSucceeded reflects BOTH the rolling DMA state machine's own outcome AND
        // whether every DMA/RSI/RVOL/ATR write this run actually succeeded (2026-08-01 audit
        // fix - closes the false-success gap: before this fix, all 160 of these writes could
        // fail, as they did on 8/1, while AllSourcesSucceeded still recorded true, because
        // this flag was wired only to rollingDmaStateMachineSucceeded above). Strict
        // threshold - ANY indicator-write failure flips this to false, not just a failure
        // rate above some cutoff - matching the existing precedent in this codebase
        // (RegimeIngestionRunner.cs: "var allSourcesSucceeded = failedCount == 0;", CLAUDE.md's
        // "Regime Ingestion: Partial-Success Guard Gap - Closed", 2026-07-28), the only other
        // place in DRPS that already answers "how many of N independent things must succeed
        // for this run to count as fully successful."
        var indicatorComputeSucceeded = indicatorWritesFailed == 0;
        var allSourcesSucceeded = rollingDmaStateMachineSucceeded && indicatorComputeSucceeded;

        var failureDetails = new List<string>();
        if (rollingDmaFailureDetail is not null)
        {
            failureDetails.Add(rollingDmaFailureDetail);
        }
        if (!indicatorComputeSucceeded)
        {
            failureDetails.Add(
                $"DMA/RSI/RVOL/ATR compute: {indicatorWritesFailed} of {indicatorWritesAttempted} indicator " +
                $"write(s) failed across {symbolsToProcess.Count} symbol(s)");
        }
        var failureDetail = failureDetails.Count > 0 ? string.Join("; ", failureDetails) : null;

        // Recorded only once the full per-symbol pass has completed, regardless of
        // individual indicator failures already isolated/logged above - same reasoning as
        // Drps.Ingestion's Worker. Fails open - see that Worker's own comment for why.
        // allSourcesSucceeded/failureDetail reflect BOTH the rolling DMA state machine's own
        // outcome and the per-symbol indicator-write outcome above - this run still records
        // as complete (the idempotency guard's "did this run happen today" meaning is
        // unchanged), but no longer misreports a failed pass (of either kind) as
        // AllSourcesSucceeded = true (CLAUDE.md's "Regime Ingestion: Partial-Success Guard
        // Gap" fix, originally applied here only to Calculator's own rolling DMA call site,
        // now extended to the per-symbol compute loop itself per this task).
        using (var guardScope = _scopeFactory.CreateScope())
        {
            try
            {
                var guardDbContext = guardScope.ServiceProvider.GetRequiredService<DrpsDbContext>();
                await WorkerRunGuard.RecordSuccessfulRunAsync(
                    guardDbContext, WorkerName, runDate, _nowProvider(), stoppingToken,
                    allSourcesSucceeded: allSourcesSucceeded,
                    failureDetail: failureDetail);
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
