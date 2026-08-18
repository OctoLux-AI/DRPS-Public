using Drps.Ingestion.Feeders;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ingestion.Orchestration;

/// <summary>
/// Persists IRegimeFeeder output (CLAUDE.md's "Regime Data Sourcing" decision, 2026-07-26).
/// Deliberately does NOT append-blind like SectorIngestionRunner - a real, necessary
/// divergence, not an oversight. Every other raw-observation runner in this codebase
/// (Sector/Insider/ExDividend) fetches a small, bounded per-call payload (one row, or a
/// handful), so unconditional AddRange never meaningfully duplicates data. Cboe/FRED's CSV
/// exports return their ENTIRE history (9000+ rows for VIX) on every single call - blindly
/// re-inserting the full history every scheduled run would duplicate thousands of rows per
/// ticker/source, every day, forever. Instead: for each feeder, the latest already-stored
/// ObservationDate for that (Ticker, Source) pair is looked up first, and only strictly
/// newer observations are inserted. This is incremental-append idempotency, not cross-source
/// reconciliation - it never compares a FRED value against a Cboe value (that remains
/// explicitly out of scope, per the decision block), it only prevents a single source from
/// re-telling DRPS something it already recorded.
///
/// Also guarded by WorkerRunGuard (CLAUDE.md's guard-standardization task, 2026-07-24), same
/// precedent as UniverseIngestionRunner/UniverseBarSweepRunner - a crash-restart or accidental
/// manual re-trigger on the same day shouldn't re-hit all five endpoints (including the two
/// slow, 90-second-timeout FRED calls) for no reason, even though the dedup logic above would
/// still keep the DB itself correct either way.
/// </summary>
public class RegimeIngestionRunner
{
    // Identifies this Runner's rows in WorkerRunRecord - see WorkerRunGuard's own doc comment.
    private const string WorkerName = "Drps.Regime";

    private readonly IEnumerable<IRegimeFeeder> _feeders;
    private readonly DrpsDbContext _dbContext;
    private readonly ILogger<RegimeIngestionRunner> _logger;

    public RegimeIngestionRunner(IEnumerable<IRegimeFeeder> feeders, DrpsDbContext dbContext, ILogger<RegimeIngestionRunner> logger)
    {
        _feeders = feeders;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task RunAsync(DateOnly runDate, CancellationToken cancellationToken)
    {
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
                "[REGIME-INGESTION-RUNNER]: idempotency guard check failed for {RunDate} - proceeding as if not yet " +
                "run today (fail-open)",
                runDate);
        }

        if (alreadyRanToday)
        {
            _logger.LogInformation(
                "[REGIME-INGESTION-RUNNER]: already ran successfully for {RunDate} - skipping fetch", runDate);
            return;
        }

        var succeededCount = 0;
        var failedCount = 0;
        var failureDetails = new List<string>();

        foreach (var feeder in _feeders)
        {
            // Same defensive per-feeder isolation as every other multi-feeder runner in this
            // codebase (SectorIngestionRunner, IngestionRunner) - one feeder's failure, thrown
            // or reported, must never stop the others from running.
            try
            {
                var result = await feeder.FetchHistoryAsync(cancellationToken);

                if (!result.Success)
                {
                    failedCount++;
                    failureDetails.Add($"{feeder.Ticker}/{feeder.Source}: {result.ErrorMessage}");
                    _logger.LogWarning(
                        "[REGIME-INGESTION-RUNNER]: {Ticker}/{Source} failed: {ErrorMessage}",
                        feeder.Ticker, feeder.Source, result.ErrorMessage);
                    continue;
                }

                var latestStored = await _dbContext.RawRegimeObservations
                    .Where(o => o.Ticker == feeder.Ticker && o.Source == feeder.Source)
                    .OrderByDescending(o => o.ObservationDate)
                    .Select(o => (DateOnly?)o.ObservationDate)
                    .FirstOrDefaultAsync(cancellationToken);

                var newObservations = (latestStored is null
                        ? result.Observations
                        : result.Observations.Where(o => o.ObservationDate > latestStored.Value))
                    .ToList();

                if (newObservations.Count > 0)
                {
                    _dbContext.RawRegimeObservations.AddRange(newObservations);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }

                succeededCount++;
                _logger.LogInformation(
                    "[REGIME-INGESTION-RUNNER]: {Ticker}/{Source} - {NewCount} new observation(s) inserted " +
                    "({FetchedCount} fetched total, latest previously stored: {LatestStored})",
                    feeder.Ticker, feeder.Source, newObservations.Count, result.Observations.Count,
                    latestStored?.ToString() ?? "none");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedCount++;
                failureDetails.Add($"{feeder.Ticker}/{feeder.Source}: unexpected exception - {ex.Message}");
                _logger.LogError(
                    ex, "[REGIME-INGESTION-RUNNER]: unexpected exception processing {Ticker}/{Source}",
                    feeder.Ticker, feeder.Source);
            }
        }

        var allSourcesSucceeded = failedCount == 0;
        var failureDetail = failureDetails.Count > 0 ? string.Join("; ", failureDetails) : null;

        _logger.LogInformation(
            "[REGIME-INGESTION-RUNNER]: run for {RunDate} complete - {SucceededCount} feeder(s) succeeded, {FailedCount} failed",
            runDate, succeededCount, failedCount);

        // Recorded regardless of per-feeder outcome, per this codebase's "the cycle reached its
        // end" interpretation - a run is not re-attempted today just because a source failed
        // (that same-day-retry question is still open, per CLAUDE.md). What changes here:
        // allSourcesSucceeded/failureDetail make a partial failure distinctly recorded on this
        // row rather than looking identical to a full success - closes CLAUDE.md's "Regime
        // Ingestion: Partial-Success Guard Gap" (2026-07-27).
        try
        {
            await WorkerRunGuard.RecordSuccessfulRunAsync(
                _dbContext, WorkerName, runDate, DateTime.Now, cancellationToken, allSourcesSucceeded, failureDetail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[REGIME-INGESTION-RUNNER]: failed to record successful run for {RunDate} - the idempotency guard " +
                "will not recognize today's run as already complete if this runs again today",
                runDate);
        }
    }
}
