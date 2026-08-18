using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Drps.Shared.Scoring;
using Microsoft.EntityFrameworkCore;

namespace Drps.Execution.Candidates;

/// <summary>
/// Read-only candidate finder for the orchestrator task this precedes - identifies which
/// BUY-bucket GateScore rows are actually actionable right now. No side effects, no writes, no
/// calls to PreFireGateService/OrderFiringService - this class only answers "what could be
/// fired," never fires anything itself.
///
/// Idempotency for opens is exactly CLAUDE.md's Execution Layer: Sixth Design Decision - a
/// BUY-bucket GateScore is only actionable if no currently-open Position exists for that
/// ticker. That single ExitDate-is-null check covers both "already fired, waiting on fill" and
/// "already holds it," so no new schema or in-flight tracking is needed here (the in-memory
/// in-flight set for the fire-to-fill-confirmation gap is the orchestrator's own concern, per
/// that same design decision - deliberately not built in this class).
///
/// A BUY-bucket GateScore with no corresponding AdjusterAllocation row is excluded, not an
/// error: AdjusterScanService only inserts an AdjusterAllocation when a candidate is actually
/// funded (DiscoverUnallocatedBuyCandidatesAsync's own doc comment) - NotFunded/Skipped/
/// not-yet-sized candidates never get one. An inner join is therefore the correct, non-guessed
/// behavior, matching Adjuster's own established convention rather than treating a missing
/// allocation as a failure.
///
/// A ticker present in ExcludedTicker is also never actionable - CLAUDE.md's Execution Layer:
/// Fifth Design Decision's orphan-position handling (a real Alpaca position with no Ledger
/// record cannot be auto-healed, so it is excluded per-ticker from new automated action). This
/// query only reads that table; reconciliation is the sole writer.
///
/// GateScore staleness gate (CLAUDE.md's Execution Layer: Seventh Design Decision, 2026-07-23) -
/// open candidates only. A GateScore Gate stopped refreshing (error, exclusion, data gap) but
/// which happens to sit in the Buy bucket must not re-qualify as a fresh candidate forever - a
/// confirmed live failure mode (a seed NVDA GateScore sat unrefreshed for a week and
/// dry-run-fired every cycle until manually excluded). Reuses the exact same
/// TradingDayCalendar.CountElapsedTradingDays + "&gt;1 elapsed trading day = stale" threshold
/// GateScanService's own IsStaleData check already established, rather than inventing a second
/// staleness definition - a Friday ScanDate is still fresh on Monday morning (1 elapsed trading
/// day), but not on Tuesday (2). Deliberately scoped to opens only, per the design decision's
/// asymmetric-risk rationale (a stale BUY entry risks acting on dead data; a stale EXIT signal
/// only risks a delayed exit, not a new bad position) - CloseCandidateQuery/
/// PlateauDeactivationCandidateQuery are untouched.
///
/// Per-ticker dedup (CLAUDE.md's Execution Layer: Eighth Design Decision, 2026-07-23) - open
/// candidates only, same scoping rationale as the staleness gate above. Nothing upstream of this
/// class (the SQL query itself, CandidateOrchestrator) prevents more than one Buy-bucket
/// GateScore/AdjusterAllocation pair for the same ticker from reaching this method at once -
/// unlike CandidateOrchestrator's own close-side dedup, which is keyed off Position.Id and only
/// exists because a Position row is guaranteed unique per open ticker; no equivalent uniqueness
/// exists on the open side. Applied here as a strict newest-ScanDate-wins rule (the losing
/// row(s) are discarded outright, never merged field-by-field with the winner), before the
/// staleness check below runs - a row that would be discarded as a duplicate regardless of its
/// own age must never spuriously emit a staleness warning first.
/// </summary>
public class OpenCandidateQuery
{
    private readonly DrpsDbContext _dbContext;
    private readonly ILogger<OpenCandidateQuery> _logger;
    private readonly Func<DateTime> _nowProvider;

    public OpenCandidateQuery(
        DrpsDbContext dbContext,
        ILogger<OpenCandidateQuery> logger,
        Func<DateTime>? nowProvider = null)
    {
        _dbContext = dbContext;
        _logger = logger;
        // Same clock-injection seam as PreFireGateService/ReconciliationService/
        // FillConfirmationService - never DateTime.Now directly below the constructor.
        _nowProvider = nowProvider ?? (() => DateTime.Now);
    }

    public async Task<List<OpenCandidate>> GetActionableBuyCandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = await _dbContext.GateScores
            .Where(g => g.Bucket == GateBucket.Buy)
            .Where(g => !_dbContext.Positions.Any(p => p.Ticker == g.Ticker && p.ExitDate == null))
            .Where(g => !_dbContext.ExcludedTickers.Any(e => e.Ticker == g.Ticker))
            .Join(
                _dbContext.AdjusterAllocations,
                g => g.Id,
                a => a.GateScoreId,
                (g, a) => new OpenCandidate { GateScore = g, AdjusterAllocation = a })
            .ToListAsync(cancellationToken);

        // Per-ticker dedup runs first (CLAUDE.md's Execution Layer: Eighth Design Decision) - see
        // this method's own doc comment for why staleness must be checked against the deduped set,
        // not the raw one.
        var dedupedCandidates = DedupByTickerKeepingLatestScan(candidates);

        // CountElapsedTradingDays isn't SQL-translatable, so the staleness gate is applied here,
        // in memory, against the already-narrow Buy-bucket/no-open-Position/not-excluded/deduped
        // result set - not pushed into the EF query above.
        var asOf = _nowProvider();
        var freshCandidates = new List<OpenCandidate>(dedupedCandidates.Count);

        foreach (var candidate in dedupedCandidates)
        {
            var gateScore = candidate.GateScore;
            var tradingDaysStale = TradingDayCalendar.CountElapsedTradingDays(gateScore.ScanDate, asOf);

            if (tradingDaysStale > 1)
            {
                _logger.LogWarning(
                    "[OPEN-CANDIDATE-QUERY]: {Ticker}: BUY candidate (GateScoreId={GateScoreId}) filtered out for " +
                    "STALENESS - ScanDate={ScanDate} is {TradingDaysStale} trading day(s) behind {AsOf}, exceeds the " +
                    "1-trading-day staleness gate",
                    gateScore.Ticker, gateScore.Id, gateScore.ScanDate, tradingDaysStale, asOf);
                continue;
            }

            freshCandidates.Add(candidate);
        }

        return freshCandidates;
    }

    /// <summary>
    /// Strict newest-ScanDate-wins per ticker (CLAUDE.md's Execution Layer: Eighth Design
    /// Decision) - not a field-level merge. A tie on ScanDate itself (same instant, two rows)
    /// breaks on the higher GateScore.Id, since a higher, auto-generated Id is the more recently
    /// inserted row - deterministic rather than an arbitrary/unstable pick.
    /// </summary>
    private List<OpenCandidate> DedupByTickerKeepingLatestScan(List<OpenCandidate> candidates)
    {
        var deduped = new List<OpenCandidate>(candidates.Count);

        foreach (var group in candidates.GroupBy(c => c.GateScore.Ticker))
        {
            var ordered = group
                .OrderByDescending(c => c.GateScore.ScanDate)
                .ThenByDescending(c => c.GateScore.Id)
                .ToList();

            var kept = ordered[0];
            deduped.Add(kept);

            for (var i = 1; i < ordered.Count; i++)
            {
                var discarded = ordered[i].GateScore;

                _logger.LogWarning(
                    "[OPEN-CANDIDATE-QUERY]: {Ticker}: BUY candidate (GateScoreId={DiscardedGateScoreId}, " +
                    "ScanDate={DiscardedScanDate}) discarded for SUPERSEDED-DUPLICATE - superseded by a newer scan " +
                    "of the same ticker (GateScoreId={KeptGateScoreId}, ScanDate={KeptScanDate})",
                    discarded.Ticker, discarded.Id, discarded.ScanDate, kept.GateScore.Id, kept.GateScore.ScanDate);
            }
        }

        return deduped;
    }
}
