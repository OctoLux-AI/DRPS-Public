using Drps.Ingestion.Orchestration;
using Drps.Shared.Scheduling;

namespace Drps.Ingestion;

/// <summary>
/// Separate, independent daily loop for earnings-calendar ingestion (FinnhubEarningsFeeder) -
/// same "own hosted BackgroundService per entity domain" shape as SectorWorker/UniverseWorker,
/// not folded into Worker's own intraday loop. Built 2026-08-02, closing a real gap:
/// FinnhubEarningsFeeder was built and unit-tested 2026-07-19 but never registered or scheduled,
/// which left RawEarningsObservations permanently empty and - per CLAUDE.md's Earnings Blackout
/// Gate Decision (2026-07-19) - capped GateQualityScorer's output at WATCH unconditionally for
/// every candidate, every scan, since 2026-07-19. Confirmed 2026-08-02 that Finnhub's
/// /calendar/earnings endpoint is NOT plan-gated on the current key (real 200 responses, real
/// data) - this was purely a wiring gap, not a data-availability one.
///
/// Retimed to 03:25 (CLAUDE.md's "FIX: Repoint EarningsWorker to Calculator's dynamic DMA-5-
/// aligned candidate list", 2026-08-08) - the prior 03:05 offset ran this Worker fifteen minutes
/// before Drps.Calculator's dynamic candidate list even exists (Calculator's own run is at
/// 03:20), so it was verifying earnings-blackout status for the wrong candidate pool entirely.
/// Now scheduled after Calculator's 03:20 run and ahead of Drps.Gate's 03:35 scan, so the
/// candidate list this Worker reads (via IDma5AlignedCandidateSource, below) reflects that same
/// night's real DMA-5 alignment state before Gate's earnings-blackout hard gate
/// (GateQualityScorer.IsEarningsDataUnverified, read via EarningsLookupService) needs it.
///
/// Ticker source changed from the static 40-symbol Watchlist to IDma5AlignedCandidateSource
/// (raw SqlClient against RollingDmaStates, no ProjectReference to Drps.Calculator - see that
/// interface's own doc comment) for the same reason as the retime: verifying earnings blackout
/// for the static Watchlist instead of Gate's real candidate pool means checking the wrong
/// tickers.
///
/// On-demand worker-name targeting (CLAUDE.md's "On-Demand Worker Targeting: --run-now
/// Worker-Name Filter + Clean Exit", 2026-07-27, corrected 2026-07-28 - see "Named On-Demand
/// Worker Targeting: Host-Wide StopApplication() Corrected to Per-Worker Exit" addendum): this
/// class has no bare "--run-now" path, same as RegimeWorker/SectorWorker/UniverseWorker - only
/// "--run-now=Drps.Earnings" (matched against WorkerName below, the same identifier
/// EarningsIngestionRunner already uses for its own WorkerRunRecord rows) triggers a manual run.
///
/// A named run ends only this Worker's own ExecuteAsync task, never
/// IHostApplicationLifetime.StopApplication() - this class is co-hosted alongside several other
/// BackgroundServices in the same Drps.Ingestion process, and a host-wide stop would silently end
/// every sibling's own pending scheduled run too (the confirmed 2026-07-27 bug the 2026-07-28
/// correction fixed).
/// </summary>
public class EarningsWorker : BackgroundService
{
    // Matches EarningsIngestionRunner's own private WorkerName constant.
    private const string WorkerName = "Drps.Earnings";

    // 25 minutes after the shared 03:00 default - see this class's own doc comment for why.
    private static readonly TimeSpan RunTime = NextRunTimeCalculator.DailyRunTime + TimeSpan.FromMinutes(25);

    private readonly ILogger<EarningsWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<DateTime> _nowProvider;
    private readonly bool _runNowNamed;

    public EarningsWorker(
        ILogger<EarningsWorker> logger,
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
            await RunEarningsCycleAsync(isManual: true, runNumber: 0, stoppingToken);

            _logger.LogInformation(
                "[EARNINGS-WORKER]: named on-demand run for {WorkerName} complete - this Worker's execution ends here; " +
                "other co-hosted workers in this process are unaffected and continue on their own schedules",
                WorkerName);
            return;
        }

        // Ends only via stoppingToken cancellation (Ctrl+C, host shutdown) - never on its own,
        // same as every other daily-cadence Worker in this codebase.
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _nowProvider();
            var nextRunAt = NextRunTimeCalculator.GetNextRunTime(now, RunTime);
            var delay = nextRunAt - now;

            _logger.LogInformation(
                "[EARNINGS-WORKER]: next scheduled run at {NextRunAt} (in {Delay})", nextRunAt, delay);

            await Task.Delay(delay, stoppingToken);

            runNumber++;
            await RunEarningsCycleAsync(isManual: false, runNumber, stoppingToken);
        }
    }

    // Shared by both the named on-demand trigger and the scheduled while loop above - only the
    // log wording branches on isManual, same convention as every other Worker in this codebase.
    private async Task RunEarningsCycleAsync(bool isManual, int runNumber, CancellationToken stoppingToken)
    {
        var runDate = DateOnly.FromDateTime(_nowProvider());

        if (isManual)
        {
            _logger.LogInformation("[EARNINGS-WORKER]: starting manual/on-demand run for {RunDate}", runDate);
        }
        else
        {
            _logger.LogInformation("[EARNINGS-WORKER]: starting scheduled run {RunNumber}", runNumber);
        }

        try
        {
            IReadOnlyList<string> candidateTickers;
            // DI-lifetime-violation fix (this session): IDma5AlignedCandidateSource is
            // registered AddScoped (Program.cs), but EarningsWorker itself is a singleton
            // IHostedService - resolving it directly via constructor injection would be a
            // captive-dependency violation. Resolved fresh from its own scope here instead,
            // same IServiceScopeFactory.CreateScope() pattern already used below for
            // EarningsIngestionRunner.
            using (var candidateScope = _scopeFactory.CreateScope())
            {
                var candidateSource = candidateScope.ServiceProvider.GetRequiredService<IDma5AlignedCandidateSource>();
                candidateTickers = await candidateSource.GetDma5AlignedTickersAsync(stoppingToken);
            }

            if (candidateTickers.Count == 0)
            {
                _logger.LogInformation(
                    "[EARNINGS-WORKER]: no DMA-5-aligned candidate ticker(s) found for {RunDate} - nothing to verify this run",
                    runDate);
            }

            // Always called, even with zero candidates - EarningsIngestionRunner's own
            // per-ticker loop already no-ops cleanly on an empty list and still records the
            // WorkerRunGuard success row for the day, same "the cycle still reached its end"
            // convention Drps.Calculator's own Worker.cs uses for its identical empty-candidate
            // case, rather than special-casing an early skip here that would leave today's guard
            // unrecorded.
            using var scope = _scopeFactory.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<EarningsIngestionRunner>();
            await runner.RunAsync(candidateTickers, runDate, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // A real shutdown request, not a data-fetching failure - must propagate so the
            // host actually stops, not get logged-and-continued like the catch below.
            throw;
        }
        catch (Exception ex)
        {
            // This run's failure must not crash the loop or block future days - logged, then
            // back to computing the next scheduled target.
            _logger.LogError(ex, "[EARNINGS-WORKER]: earnings run failed for {RunDate}", runDate);
        }

        if (isManual)
        {
            _logger.LogInformation("[EARNINGS-WORKER]: manual/on-demand run for {RunDate} complete", runDate);
        }
        else
        {
            _logger.LogInformation(
                "[EARNINGS-WORKER]: scheduled run {RunNumber} complete for {RunDate}", runNumber, runDate);
        }
    }
}
