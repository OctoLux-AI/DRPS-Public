namespace Drps.Shared.Notifications;

/// <summary>
/// Default, do-nothing implementation of <see cref="ILifecycleNotificationService"/> - see
/// that interface's own doc comment. Real notification wiring (e.g. Pushover) is explicitly
/// future scope; this exists only so LedgerLifecycleStampService always has something to call.
/// </summary>
public class NoOpLifecycleNotificationService : ILifecycleNotificationService
{
    public Task NotifyLifecycleStampAsync(LifecycleStampNotification notification, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
