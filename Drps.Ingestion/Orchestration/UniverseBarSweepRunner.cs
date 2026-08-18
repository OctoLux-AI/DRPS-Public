using Drps.Ingestion.Feeders;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ingestion.Orchestration;

/// <summary>
/// Nightly full-universe OHLCV bar pull - the prerequisite the Rolling DMA State Machine and
/// Drps.Monolith's future before/after windows both depend on (CLAUDE.md's "Ingestion
/// Eligibility: No Pool Bifurcation" and "Two-Tier Verification Cost Model" decisions). Reads
/// that day's <see cref="UniverseSnapshot"/> in full - every ticker in the snapshot gets a
/// bar-pull attempt, unconditionally. No DMA pre-screen, no watchlist, no eligibility gate of
/// any kind: per "No Pool Bifurcation," ingestion sees the full universe before any pool forms
/// an opinion.
///
/// Single-source (Alpaca only), by design - the Two-Tier Verification Cost Model's cheap
/// nightly tier. Bars land in <see cref="RawOhlcvBar"/> with no corresponding
/// <see cref="BarVerification"/> row ever created here; every downstream verification-join
/// service (DmaVerificationJoinService and its Rsi/Rvol/Atr siblings) already treats "no
/// BarVerification row at all" as unverified/false, fail-closed - so leaving that table
/// entirely untouched IS the "tagged verified=false" behavior this tier calls for, not a gap.
/// Dual-source verification for the narrower, DMA-aligned candidate pool remains a separate,
/// already-existing path (BarReconciliationService, driven by Worker/IngestionJob against the
/// watchlist) that this class never calls.
/// </summary>
public class UniverseBarSweepRunner
{
    // The real, empirically-confirmed batch ceiling - CLAUDE.md's "Alpaca Bandwidth —
    // Empirical Findings (locked)" block (2026-07-22): 500 symbols/call came back clean (no
    // next_page_token) across every batch size tested up to 2000; 1000+ triggered pagination
    // against Alpaca's ~10,000-data-point response cap. Not a guessed placeholder - this is
    // the real number a live probe against the actual account produced.
    public const int ConfirmedMaxBatchSize = 500;

    // Identifies this Runner's rows in WorkerRunRecord - see WorkerRunGuard's own doc comment.
    // Distinct from UniverseIngestionRunner's "Drps.UniverseSnapshot" (a separate cycle with its
    // own completion) so the two guards never collide in the same shared table.
    private const string WorkerName = "Drps.UniverseBarSweep";

    private readonly IAlpacaBatchBarFeeder _feeder;
    private readonly DrpsDbContext _dbContext;
    private readonly ILogger<UniverseBarSweepRunner> _logger;

    public UniverseBarSweepRunner(IAlpacaBatchBarFeeder feeder, DrpsDbContext dbContext, ILogger<UniverseBarSweepRunner> logger)
    {
        _feeder = feeder;
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task RunAsync(DateOnly snapshotDate, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        // Idempotency guard (CLAUDE.md's guard-standardization task, 2026-07-24) - this Runner
        // previously had none at all, relying solely on downstream append-only dedup to make a
        // duplicate run harmless. That's true for data correctness but doesn't stop a
        // crash-restart or accidental manual re-trigger from re-hitting all ~12,479 symbols
        // against Alpaca for no reason. Same fail-open shape as every other guard-check call
        // site in this codebase: a guard-check failure must never itself become a reason a
        // legitimate run is blocked.
        var alreadyRanToday = false;
        try
        {
            alreadyRanToday = await WorkerRunGuard.HasRunTodayAsync(_dbContext, WorkerName, snapshotDate, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[UNIVERSE-BAR-SWEEP]: idempotency guard check failed for {SnapshotDate} - proceeding as if not yet " +
                "run today (fail-open)",
                snapshotDate);
        }

        if (alreadyRanToday)
        {
            _logger.LogInformation(
                "[UNIVERSE-BAR-SWEEP]: already ran successfully for {SnapshotDate} - skipping sweep", snapshotDate);
            return;
        }

        var tickers = await _dbContext.UniverseSnapshots
            .Where(s => s.SnapshotDate == snapshotDate)
            .Select(s => s.Ticker)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (tickers.Count == 0)
        {
            // Fail-closed: no snapshot for this date (UniverseWorker hasn't run yet, or its
            // own fetch failed for the night) means nothing to sweep. No fallback to a stale
            // prior-day snapshot - the next scheduled run picks this back up, same "stale or
            // missing blocks, never passes silently" rule as everywhere else in this codebase.
            _logger.LogWarning(
                "[UNIVERSE-BAR-SWEEP]: no UniverseSnapshot rows found for {SnapshotDate} - nothing to sweep", snapshotDate);

            // Deliberately does NOT record success here, unlike the end-of-sweep path below:
            // zero Alpaca calls were made (there is nothing yet to protect against re-hitting),
            // and recording success now would block a legitimate later-today retry once the
            // snapshot this sweep depends on actually lands - the exact "genuinely no work was
            // attempted" case the guard shouldn't paper over.
            return;
        }

        // Visibility only, per CLAUDE.md's fail-closed-everywhere principle - a known-partial
        // day's surviving tickers are still swept exactly as before (no behavior change), this
        // only makes the gap loud instead of silent. A missing coverage row (e.g. a date
        // predating this check) is not itself treated as a problem - fails open to the existing
        // behavior, same as every other visibility-only addition in this codebase.
        var coverage = await _dbContext.UniverseSnapshotCoverages
            .SingleOrDefaultAsync(c => c.SnapshotDate == snapshotDate, cancellationToken);
        if (coverage is not null && coverage.SourcesSucceeded != UniverseSourceCoverage.All)
        {
            _logger.LogWarning(
                "[UNIVERSE-BAR-SWEEP]: {SnapshotDate}'s universe is KNOWN-PARTIAL (sources succeeded: {SourcesSucceeded}) " +
                "- sweeping the {TickerCount} ticker(s) actually present, but at least one exchange's tickers are " +
                "silently absent from tonight's universe",
                snapshotDate, coverage.SourcesSucceeded, tickers.Count);
        }

        var succeededCount = 0;
        var noDataCount = 0;
        var failedCount = 0;
        var batchNumber = 0;

        foreach (var batch in Chunk(tickers, ConfirmedMaxBatchSize))
        {
            batchNumber++;

            BatchFeedFetchResult result;
            try
            {
                result = await _feeder.FetchBatchAsync(batch, start, end, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A real shutdown request, not a fetch failure - must propagate, not be
                // swallowed into "this batch failed."
                throw;
            }
            catch (Exception ex)
            {
                // Batch-level isolation: one batch's unexpected throw must not abort the rest
                // of the sweep - logged once for the batch, then continues to the next one.
                _logger.LogError(
                    ex, "[UNIVERSE-BAR-SWEEP]: batch {BatchNumber} ({Count} tickers) threw unexpectedly",
                    batchNumber, batch.Count);
                LogEachAsFailed(batch, batchNumber, ex.Message);
                failedCount += batch.Count;
                continue;
            }

            if (!result.Success)
            {
                // Fail-closed PER TICKER, not per batch: the sweep itself continues to the
                // next batch regardless, but every individual ticker in this failed batch
                // still gets its own log trace - never a single aggregate "batch N failed"
                // line standing in for 500 tickers' worth of missing data.
                LogEachAsFailed(batch, batchNumber, result.ErrorMessage ?? "unknown error");
                failedCount += batch.Count;
                continue;
            }

            if (result.NextPageTokenPresent)
            {
                // Known, documented gap (see AlpacaBatchBarFeeder's own doc comment) - not
                // expected to fire at this batch size/date-range shape, but if it does, only
                // this batch's first page was persisted below.
                _logger.LogWarning(
                    "[UNIVERSE-BAR-SWEEP]: batch {BatchNumber} response was paginated (next_page_token present) - " +
                    "pagination is not implemented, only the first page's bars are persisted",
                    batchNumber);
            }

            var barsToInsert = new List<RawOhlcvBar>();
            foreach (var ticker in batch)
            {
                if (result.BarsBySymbol.TryGetValue(ticker, out var bars) && bars.Count > 0)
                {
                    barsToInsert.AddRange(bars);
                    succeededCount++;
                }
                else
                {
                    // A ticker with no valid bar returned (delisted mid-day, halted, genuinely
                    // no trading in this window, etc.) - logged individually, never silently
                    // skipped without a trace. Ambiguous by nature, same shape as
                    // AlpacaFeeder's own NoDataForRange case - not treated as an error.
                    _logger.LogInformation(
                        "[UNIVERSE-BAR-SWEEP]: no bars returned for {Ticker} in batch {BatchNumber}", ticker, batchNumber);
                    noDataCount++;
                }
            }

            if (barsToInsert.Count > 0)
            {
                _dbContext.RawOhlcvBars.AddRange(barsToInsert);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        _logger.LogInformation(
            "[UNIVERSE-BAR-SWEEP]: sweep for {SnapshotDate} complete - {Total} tickers, {Succeeded} succeeded, " +
            "{NoData} no data, {Failed} failed, {BatchCount} batch(es)",
            snapshotDate, tickers.Count, succeededCount, noDataCount, failedCount, batchNumber);

        // Recorded regardless of per-batch outcome (succeeded/no-data/failed) - same "the cycle
        // reached its end" interpretation of "ran successfully" as every other guard-recording
        // call site in this codebase (Worker.cs's per-symbol loop, UniverseIngestionRunner's own
        // both-sources-failed path). Per-batch failures are already isolated/logged above; a bad
        // batch does not itself withhold this.
        try
        {
            await WorkerRunGuard.RecordSuccessfulRunAsync(_dbContext, WorkerName, snapshotDate, DateTime.Now, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[UNIVERSE-BAR-SWEEP]: failed to record successful run for {SnapshotDate} - the idempotency guard " +
                "will not recognize today's run as already complete if this runs again today",
                snapshotDate);
        }
    }

    private void LogEachAsFailed(IReadOnlyList<string> batch, int batchNumber, string reason)
    {
        foreach (var ticker in batch)
        {
            _logger.LogWarning(
                "[UNIVERSE-BAR-SWEEP]: no bars persisted for {Ticker} - batch {BatchNumber} failed: {Reason}",
                ticker, batchNumber, reason);
        }
    }

    private static IEnumerable<List<string>> Chunk(IReadOnlyList<string> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.Skip(i).Take(size).ToList();
    }
}
