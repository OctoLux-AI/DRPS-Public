using Drps.Ingestion.Feeders;
using Drps.Ingestion.Persistence;
using Drps.Shared.Exceptions;

namespace Drps.Ingestion.Orchestration;

/// <summary>
/// Cycle-level wrapper around FinnhubEarningsFeeder - same WorkerRunGuard shape as
/// UniverseIngestionRunner/UniverseBarSweepRunner (guard checked once per calendar day, covering
/// the whole watchlist sweep, not per-ticker), rather than SectorIngestionRunner/
/// ExDividendIngestionRunner's per-ticker-call shape with no cycle-level guard at all. Earnings
/// data has no per-symbol "already tried this ticker today" concern the way sector/ex-dividend
/// ingestion accumulates across separate per-ticker DI scopes - one guard covering the full
/// watchlist sweep is the correct granularity here, matching this task's own stated precedent.
///
/// FinnhubEarningsFeeder is deliberately self-persisting (its own doc comment) - every call
/// writes exactly one RawEarningsObservation row, success or failure, so this Runner does no
/// persistence of its own beyond the WorkerRunRecord guard row itself.
/// </summary>
public class EarningsIngestionRunner
{
    // Identifies this Runner's rows in WorkerRunRecord - see WorkerRunGuard's own doc comment.
    // Matches EarningsWorker's own WorkerName constant (see that class's doc comment for why),
    // same convention as UniverseIngestionRunner/UniverseIngestionWorker's shared identifier.
    private const string WorkerName = "Drps.Earnings";

    private readonly FinnhubEarningsFeeder _feeder;
    private readonly DrpsDbContext _dbContext;
    private readonly ILogger<EarningsIngestionRunner> _logger;

    public EarningsIngestionRunner(FinnhubEarningsFeeder feeder, DrpsDbContext dbContext, ILogger<EarningsIngestionRunner> logger)
    {
        _feeder = feeder;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task RunAsync(IReadOnlyList<string> tickers, DateOnly runDate, CancellationToken cancellationToken)
    {
        // Idempotency guard (CLAUDE.md's guard-standardization task, 2026-07-24) - same
        // fail-open shape as every other guard-check call site in this codebase: a guard-check
        // failure must never itself become a reason a legitimate run is blocked.
        var alreadyRanToday = false;
        try
        {
            alreadyRanToday = await WorkerRunGuard.HasRunTodayAsync(_dbContext, WorkerName, runDate, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[EARNINGS-RUNNER]: idempotency guard check failed for {RunDate} - proceeding as if not yet run " +
                "today (fail-open)",
                runDate);
        }

        if (alreadyRanToday)
        {
            _logger.LogInformation(
                "[EARNINGS-RUNNER]: already ran successfully for {RunDate} - skipping fetch", runDate);
            return;
        }

        var succeededCount = 0;
        var failedCount = 0;

        foreach (var ticker in tickers)
        {
            try
            {
                await _feeder.FetchNextEarningsAsync(ticker, cancellationToken);
                succeededCount++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A real shutdown request, not a fetch failure - must propagate so the host
                // actually stops, not get logged-and-continued like the catch below.
                throw;
            }
            catch (ConfigurationMissingException ex)
            {
                // A missing API key means no HTTP call was ever attempted, for this ticker or
                // any of the remaining ones in this loop - every subsequent ticker would fail
                // identically, so this aborts the whole run immediately rather than logging the
                // same configuration error once per ticker. Deliberately does NOT call
                // RecordSuccessfulRunAsync below: a future run (once the key is configured) must
                // not be blocked by today's guard row when nothing was actually attempted.
                _logger.LogError(ex,
                    "[EARNINGS-RUNNER]: aborting run for {RunDate} - missing configuration, no fetch was attempted " +
                    "for {Ticker} or any remaining ticker this cycle",
                    runDate, ticker);
                return;
            }
            catch (Exception ex)
            {
                // Per-ticker isolation - one ticker's unexpected failure must not stop the rest
                // of the sweep. FinnhubEarningsFeeder already collapses its own fetch/parse
                // failures into an explicit Verified=false row rather than throwing, so reaching
                // this branch means something genuinely unexpected happened (e.g. a DB save
                // failure) - logged and the loop continues to the next ticker regardless.
                failedCount++;
                _logger.LogError(ex, "[EARNINGS-RUNNER]: unexpected exception fetching earnings for {Ticker}", ticker);
            }
        }

        _logger.LogInformation(
            "[EARNINGS-RUNNER]: run for {RunDate} complete - {SucceededCount} of {TotalCount} ticker(s) processed, " +
            "{FailedCount} unexpected failure(s)",
            runDate, succeededCount, tickers.Count, failedCount);

        // Recorded regardless of per-ticker outcome, same "the cycle reached its end"
        // interpretation as UniverseBarSweepRunner's own guard-recording call site - a per-ticker
        // failure is already isolated/logged above, and FinnhubEarningsFeeder's own fail-closed
        // Verified=false row is itself a completed, recorded outcome for that ticker, not a gap.
        try
        {
            await WorkerRunGuard.RecordSuccessfulRunAsync(_dbContext, WorkerName, runDate, DateTime.Now, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[EARNINGS-RUNNER]: failed to record successful run for {RunDate} - the idempotency guard will not " +
                "recognize today's run as already complete if this runs again today",
                runDate);
        }
    }
}
