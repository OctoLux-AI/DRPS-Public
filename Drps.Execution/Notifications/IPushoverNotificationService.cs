namespace Drps.Execution.Notifications;

/// <summary>
/// Real-channel push notification sender (CLAUDE.md's Execution Layer: Ninth Design Decision -
/// Pushover Notification Wiring). Wired into four trigger points across three classes:
/// OrderFiringService (a genuine, non-dry-run fire succeeds, open or close; and, since the
/// 2026-07-31 retry-ambiguity audit, a fire that resolves to OrderFiringOutcome.
/// AmbiguousUnresolved - the original PlaceOrderAsync call and its one allowed retry both came
/// back ambiguous, so fill state cannot be confirmed either way), ReconciliationService
/// (orphan/phantom detection), and PreFireGateService (a kill-switch trip). Deliberately NOT
/// wired to dry-run log lines, OpenCandidateQuery's staleness/dedup filtering, or
/// WeeklyVarianceAuditService - explicitly out of scope for this decision.
///
/// Distinct from Drps.Shared.Notifications.ILifecycleNotificationService (the Gate/Ledger
/// lifecycle-stamp carve-out's own, still-NoOp seam, added 2026-07-18) - that interface is not
/// wired to this implementation; the two are separate, independently-scoped notification paths.
///
/// A caller must never see an exception propagate from this call, and a missing/placeholder
/// credential must never block the caller's own real work (an order fire, a reconciliation
/// pass, a pre-fire-gate check) - every implementation is responsible for catching and logging
/// internally, never throwing.
/// </summary>
public interface IPushoverNotificationService
{
    Task SendAsync(string message, CancellationToken cancellationToken);
}
