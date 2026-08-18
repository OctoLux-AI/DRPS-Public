namespace Drps.Shared.Notifications;

/// <summary>
/// Ticker/PositionId/reason for a lifecycle-field stamp (e.g. LowGradeDate/CoolDownStartDate
/// together on a composite-degradation exit). AsOf is the same recommendation-time timestamp
/// stamped onto the Position row, not a notification-send time.
/// </summary>
public sealed record LifecycleStampNotification(string Ticker, long PositionId, string StampReason, DateTime AsOf);

/// <summary>
/// Structural seam only - NOT wired to fire real notifications yet. Added per Kent's request
/// (CLAUDE.md's "Gate/Adjuster→Ledger Lifecycle-Stamp Carve-Out: Resolved (2026-07-18)" block,
/// point 6) so a future task has somewhere to plug in Pushover (or any other channel) without
/// touching LedgerLifecycleStampService's own write-path logic. This does NOT change or
/// advance the existing "Pushover parked until 30 auto-completed trades" decision (CLAUDE.md,
/// Known Open Questions) - that gate still governs when any real notification fires. The only
/// implementation registered today, <see cref="NoOpLifecycleNotificationService"/>, does
/// nothing.
/// </summary>
public interface ILifecycleNotificationService
{
    Task NotifyLifecycleStampAsync(LifecycleStampNotification notification, CancellationToken cancellationToken);
}
