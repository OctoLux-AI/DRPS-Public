using Drps.Execution.Notifications;

namespace Drps.Tests.TestHelpers;

// Hand-rolled IPushoverNotificationService fake, same convention as FakeAlpacaTradingClient -
// no Moq. Records every sent message so a test can assert a notification fired (or didn't)
// without making a real HTTP call to Pushover.
public class FakePushoverNotificationService : IPushoverNotificationService
{
    public readonly List<string> SentMessages = new();

    public Task SendAsync(string message, CancellationToken cancellationToken)
    {
        SentMessages.Add(message);
        return Task.CompletedTask;
    }
}
