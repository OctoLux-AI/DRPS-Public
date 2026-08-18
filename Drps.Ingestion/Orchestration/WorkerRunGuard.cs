using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ingestion.Orchestration;

/// <summary>
/// Idempotency guard against the backward-clock re-fire bug found in this session's
/// scheduling-resilience audit (Scenario 3): a backward wall-clock correction landing between
/// the corrected time and today's already-passed scheduled run time can cause
/// NextRunTimeCalculator to compute that same calendar day's target a second time, re-firing
/// the whole day's work. One WorkerRunRecord row per successful scheduled run, append-only
/// (matches this codebase's Immutability cornerstone) - "already ran today" is answered by row
/// existence, not by a mutable per-worker row updated in place. Deliberately DB-only, no
/// distributed lock: a single workstation process is the only realistic deployment shape today
/// (per the audit), so a simple row-existence check is sufficient - a genuinely concurrent
/// multi-instance deployment would need a different mechanism, not built here.
///
/// Pure DB access, no logging or error-handling of its own - each Worker wraps these calls in
/// its own existing try/catch idiom, same as every other DI-resolved call site in these
/// classes, rather than this helper imposing its own policy.
/// </summary>
public static class WorkerRunGuard
{
    public static Task<bool> HasRunTodayAsync(
        DrpsDbContext dbContext, string workerName, DateOnly runDate, CancellationToken cancellationToken) =>
        dbContext.WorkerRunRecords.AnyAsync(r => r.WorkerName == workerName && r.RunDate == runDate, cancellationToken);

    // allSourcesSucceeded/failureDetail are optional and default to a full-success row with no
    // detail - every pre-existing caller (single-source Workers, WeeklyVarianceAuditWorker,
    // UniverseIngestionRunner, UniverseBarSweepRunner) is unaffected and unchanged.
    // RegimeIngestionRunner is the first caller to pass allSourcesSucceeded: false, so a partial
    // failure across its five IRegimeFeeder sources is recorded distinctly rather than looking
    // identical to a full success (CLAUDE.md's "Regime Ingestion: Partial-Success Guard Gap",
    // 2026-07-27).
    public static async Task RecordSuccessfulRunAsync(
        DrpsDbContext dbContext, string workerName, DateOnly runDate, DateTime completedAt, CancellationToken cancellationToken,
        bool allSourcesSucceeded = true, string? failureDetail = null)
    {
        dbContext.WorkerRunRecords.Add(new WorkerRunRecord
        {
            WorkerName = workerName,
            RunDate = runDate,
            CompletedAt = completedAt,
            AllSourcesSucceeded = allSourcesSucceeded,
            FailureDetail = failureDetail
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
