using Drps.Ingestion.Orchestration;
using Drps.Shared;
using Drps.Shared.Scheduling;
using Microsoft.Extensions.Options;

namespace Drps.Ingestion;

/// <summary>
/// Separate, independent daily loop for ex-dividend ingestion - deliberately not folded
/// into Worker's intraday loop. Ex-dividend data has a 7-day staleness TTL (per CLAUDE.md's
/// Value + Provenance pattern), so Worker's 15-minute cadence would be wasteful; a
/// once-daily run at 03:00 local time comfortably clears that TTL with margin even if a
/// single run is missed.
///
/// On-demand worker-name targeting (CLAUDE.md's "On-Demand Worker Targeting: --run-now
/// Worker-Name Filter + Clean Exit", 2026-07-27, corrected 2026-07-28 - see "Named On-Demand
/// Worker Targeting: Host-Wide StopApplication() Corrected to Per-Worker Exit" addendum): this
/// class has no bare "--run-now" path, same as SectorWorker - only "--run-now=Drps.ExDividend"
/// triggers a manual run. Neither this class nor ExDividendIngestionRunner previously had a
/// WorkerRunGuard identifier at all (ex-dividend ingestion has no idempotency guard today) -
/// WorkerName below is a new constant, added specifically to serve as this Worker's on-demand
/// matching target, following the exact "Drps.X" naming convention every other worker in this
/// codebase already uses. It does not add or change any idempotency-guard behavior - that
/// remains out of scope for this decision.
///
/// A named run ends only this Worker's own ExecuteAsync task, never
/// IHostApplicationLifetime.StopApplication() - this class is co-hosted alongside six other
/// BackgroundServices in the same Drps.Ingestion process, and a host-wide stop would silently
/// end every sibling's own pending scheduled run too (the confirmed 2026-07-27 bug this
/// correction fixes).
/// </summary>
public class ExDividendWorker : BackgroundService
{
    // Dividend events are sparse (typically quarterly), unlike Worker's daily-bar fetch
    // range - a narrow window would frequently return nothing. Not exposed as a config
    // setting (this task is scoped to scheduling only): 95 days back safely spans a full
    // fiscal quarter plus margin for reporting lag; 30 days forward catches a near-future
    // ex-date Finnhub has already declared. Revisit if real data shows this needs tuning.
    private const int LookbackDays = 95;
    private const int LookForwardDays = 30;

    // On-demand matching target only (see this class's own doc comment) - not a
    // WorkerRunGuard identifier, since ex-dividend ingestion has no idempotency guard.
    private const string WorkerName = "Drps.ExDividend";

    private readonly ILogger<ExDividendWorker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IngestionSettings _settings;
    private readonly WatchlistOptions _watchlist;
    private readonly Func<DateTime> _nowProvider;
    private readonly bool _runNowNamed;

    public ExDividendWorker(
        ILogger<ExDividendWorker> logger,
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
        // No clock abstraction exists elsewhere in this codebase (Worker.cs calls
        // DateTime.UtcNow directly) - this optional constructor parameter is the minimal
        // hand-rolled seam needed to make the 03:00 wait testable without a new package,
        // matching this codebase's existing preference for hand-rolled test seams over
        // adding a dependency (e.g. FakeHttpMessageHandler instead of a mocking library).
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
            await RunExDividendCycleAsync(isManual: true, runNumber: 0, stoppingToken);

            _logger.LogInformation(
                "[EXDIV-WORKER]: named on-demand run for {WorkerName} complete - this Worker's execution ends here; " +
                "other co-hosted workers in this process are unaffected and continue on their own schedules",
                WorkerName);
            return;
        }

        // Ends only via stoppingToken cancellation (Ctrl+C, host shutdown) - never on its
        // own, same as Worker's intraday loop.
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _nowProvider();
            var nextRunAt = NextRunTimeCalculator.GetNextRunTime(now);
            var delay = nextRunAt - now;

            _logger.LogInformation(
                "[EXDIV-WORKER]: next scheduled run at {NextRunAt} (in {Delay})", nextRunAt, delay);

            await Task.Delay(delay, stoppingToken);

            runNumber++;
            await RunExDividendCycleAsync(isManual: false, runNumber, stoppingToken);
        }
    }

    // Shared by both the named on-demand trigger and the scheduled while loop above - only the
    // log wording branches on isManual, same convention as every other Worker in this codebase.
    private async Task RunExDividendCycleAsync(bool isManual, int runNumber, CancellationToken stoppingToken)
    {
        if (isManual)
        {
            _logger.LogInformation("[EXDIV-WORKER]: starting manual/on-demand run");
        }
        else
        {
            _logger.LogInformation("[EXDIV-WORKER]: starting scheduled run {RunNumber}", runNumber);
        }

        var end = DateOnly.FromDateTime(_nowProvider());
        var start = end.AddDays(-LookbackDays);
        var lookForwardEnd = end.AddDays(LookForwardDays);

        var processedCount = 0;

        foreach (var symbol in _watchlist.Symbols)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<ExDividendIngestionRunner>();
                await runner.RunAsync(symbol, start, lookForwardEnd, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // A real shutdown request, not a data-fetching failure - must propagate so
                // the host actually stops, not get logged-and-continued like the catch below.
                throw;
            }
            catch (Exception ex)
            {
                // One symbol's failure (or any other unexpected exception during this
                // day's run) must not crash the loop or block future days - logged and
                // the loop simply continues to the next symbol, then back to computing
                // the next 03:00 target once the watchlist is done.
                _logger.LogError(ex, "[EXDIV-WORKER]: ex-dividend run failed for {Symbol}", symbol);
            }

            processedCount++;
        }

        if (isManual)
        {
            _logger.LogInformation(
                "[EXDIV-WORKER]: manual/on-demand run complete, processed {ProcessedCount} symbol(s)", processedCount);
        }
        else
        {
            _logger.LogInformation(
                "[EXDIV-WORKER]: scheduled run {RunNumber} complete, processed {ProcessedCount} symbol(s)",
                runNumber, processedCount);
        }
    }
}
