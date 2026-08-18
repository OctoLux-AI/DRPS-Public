using Drps.Ingestion.Orchestration;
using Drps.Shared.Scheduling;
using Microsoft.Extensions.Options;

namespace Drps.Ingestion;

/// <summary>
/// Separate, independent daily loop for sector/industry ingestion - same shape as
/// ExDividendWorker, not folded into it (a different entity domain - SectorSourceType, not
/// SourceType). Sector classification is slow-moving reference data, same 7-day staleness
/// TTL bucket as ex-dividends (per CLAUDE.md's Value + Provenance pattern) and as
/// shares-outstanding/market-cap-style fields, so Worker's 15-minute intraday cadence would
/// be wasteful; a once-daily run comfortably clears that TTL with margin even if a single
/// run is missed.
///
/// Retimed to 03:27 (CLAUDE.md's "FIX: Repoint SectorWorker to Calculator's dynamic DMA-5-
/// aligned candidate list", 2026-08-08 - was 03:00, twenty minutes before Calculator's own
/// dynamic candidate list even exists). Now scheduled after Calculator's 03:20 run - and after
/// EarningsWorker's own 03:25 run, per the same 2026-08-08 decision - so the candidate list
/// this Worker reads (via IDma5AlignedCandidateSource, below) reflects that same night's real
/// DMA-5 alignment state instead of looking up sector classification for the wrong pool.
///
/// Ticker source changed from the static 40-symbol Watchlist to IDma5AlignedCandidateSource
/// (raw SqlClient against RollingDmaStates, no ProjectReference to Drps.Calculator - see that
/// interface's own doc comment), reusing the same abstraction EarningsWorker's own 2026-08-08
/// fix already introduced rather than duplicating it.
///
/// On-demand worker-name targeting (CLAUDE.md's "On-Demand Worker Targeting: --run-now
/// Worker-Name Filter + Clean Exit", 2026-07-27, corrected 2026-07-28 - see "Named On-Demand
/// Worker Targeting: Host-Wide StopApplication() Corrected to Per-Worker Exit" addendum): this
/// class has no bare "--run-now" path, same as RegimeWorker - only "--run-now=Drps.Sector"
/// triggers a manual run. Unlike RegimeWorker/RegimeIngestionRunner, neither this class nor
/// SectorIngestionRunner previously had a WorkerRunGuard identifier at all (sector ingestion
/// has no idempotency guard today) - WorkerName below is a new constant, added specifically to
/// serve as this Worker's on-demand matching target, following the exact "Drps.X" naming
/// convention every other worker in this codebase already uses. It does not add or change any
/// idempotency-guard behavior - that remains out of scope for this decision.
///
/// A named run ends only this Worker's own ExecuteAsync task, never
/// IHostApplicationLifetime.StopApplication() - this class is co-hosted alongside six other
/// BackgroundServices in the same Drps.Ingestion process, and a host-wide stop would silently
/// end every sibling's own pending scheduled run too (the confirmed 2026-07-27 bug this
/// correction fixes).
/// </summary>
public class SectorWorker : BackgroundService
{
    // On-demand matching target only (see this class's own doc comment) - not a
    // WorkerRunGuard identifier, since sector ingestion has no idempotency guard.
    private const string WorkerName = "Drps.Sector";

    // 27 minutes after the shared 03:00 default - see this class's own doc comment for why.
    private static readonly TimeSpan RunTime = NextRunTimeCalculator.DailyRunTime + TimeSpan.FromMinutes(27);

    private readonly ILogger<SectorWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IngestionSettings _settings;
    private readonly Func<DateTime> _nowProvider;
    private readonly bool _runNowNamed;

    public SectorWorker(
        ILogger<SectorWorker> logger,
        IServiceScopeFactory scopeFactory,
        IOptions<IngestionSettings> settings,
        Func<DateTime>? nowProvider = null,
        string[]? args = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
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
            await RunSectorCycleAsync(isManual: true, runNumber: 0, stoppingToken);

            _logger.LogInformation(
                "[SECTOR-WORKER]: named on-demand run for {WorkerName} complete - this Worker's execution ends here; " +
                "other co-hosted workers in this process are unaffected and continue on their own schedules",
                WorkerName);
            return;
        }

        // Ends only via stoppingToken cancellation (Ctrl+C, host shutdown) - never on its
        // own, same as Worker's and ExDividendWorker's loops.
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _nowProvider();
            var nextRunAt = NextRunTimeCalculator.GetNextRunTime(now, RunTime);
            var delay = nextRunAt - now;

            _logger.LogInformation(
                "[SECTOR-WORKER]: next scheduled run at {NextRunAt} (in {Delay})", nextRunAt, delay);

            await Task.Delay(delay, stoppingToken);

            runNumber++;
            await RunSectorCycleAsync(isManual: false, runNumber, stoppingToken);
        }
    }

    // Shared by both the named on-demand trigger and the scheduled while loop above - only the
    // log wording branches on isManual, same convention as every other Worker in this codebase.
    private async Task RunSectorCycleAsync(bool isManual, int runNumber, CancellationToken stoppingToken)
    {
        if (isManual)
        {
            _logger.LogInformation("[SECTOR-WORKER]: starting manual/on-demand run");
        }
        else
        {
            _logger.LogInformation("[SECTOR-WORKER]: starting scheduled run {RunNumber}", runNumber);
        }

        var processedCount = 0;

        IReadOnlyList<string> candidateTickers;
        try
        {
            // DI-lifetime-violation fix (this session): IDma5AlignedCandidateSource is
            // registered AddScoped (Program.cs), but SectorWorker itself is a singleton
            // IHostedService - resolving it directly via constructor injection would be a
            // captive-dependency violation. Resolved fresh from its own scope here instead,
            // same IServiceScopeFactory.CreateScope() pattern already used below for
            // SectorIngestionRunner.
            using var candidateScope = _scopeFactory.CreateScope();
            var candidateSource = candidateScope.ServiceProvider.GetRequiredService<IDma5AlignedCandidateSource>();
            candidateTickers = await candidateSource.GetDma5AlignedTickersAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fails open to an empty candidate list rather than crashing the whole cycle -
            // same posture Drps.Calculator's own Worker.cs uses for this identical read.
            candidateTickers = Array.Empty<string>();
            _logger.LogWarning(ex,
                "[SECTOR-WORKER]: failed to read DMA-5-aligned candidate tickers from RollingDmaStates - " +
                "no candidates for this run");
        }

        if (candidateTickers.Count == 0)
        {
            _logger.LogInformation(
                "[SECTOR-WORKER]: no DMA-5-aligned candidate ticker(s) found - nothing to look up this run");
        }

        foreach (var ticker in candidateTickers)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<SectorIngestionRunner>();
                await runner.RunAsync(ticker, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // A real shutdown request, not a data-fetching failure - must propagate so
                // the host actually stops, not get logged-and-continued like the catch below.
                throw;
            }
            catch (Exception ex)
            {
                // One ticker's failure must not crash the loop or block future runs -
                // logged and the loop simply continues to the next ticker.
                _logger.LogError(ex, "[SECTOR-WORKER]: sector run failed for {Ticker}", ticker);
            }

            processedCount++;
        }

        if (isManual)
        {
            _logger.LogInformation(
                "[SECTOR-WORKER]: manual/on-demand run complete, processed {ProcessedCount} ticker(s)", processedCount);
        }
        else
        {
            _logger.LogInformation(
                "[SECTOR-WORKER]: scheduled run {RunNumber} complete, processed {ProcessedCount} ticker(s)",
                runNumber, processedCount);
        }
    }
}
