namespace Drps.Watchdog;

/// <summary>
/// Standalone Pushover alert sender, deliberately duplicated rather than shared - Drps.Watchdog
/// carries zero ProjectReferences (see the .csproj's own doc comment), so this does not reuse
/// Drps.Execution's IPushoverNotificationService/PushoverNotificationService or even Drps.
/// Shared's BuildCurrencyAlerter, despite being near-identical to both. A watchdog that could be
/// taken down by a bug or dependency problem anywhere else in the solution would defeat its own
/// purpose (CLAUDE.md's 8/12-8/13 outage - the exact class of failure this project exists to
/// catch). Same Pushover message-API shape those two classes already use (POST, form-urlencoded,
/// token/user/message).
///
/// Never throws - every path (missing credentials, non-2xx HTTP response, any exception)
/// resolves to a PushoverAlertResult instead. This program's own exit code is what signals
/// failure to whatever launches it (Task Scheduler); a Pushover-side outage must not crash the
/// watchdog itself or be mistaken for "nothing to report."
/// </summary>
public static class PushoverAlerter
{
    private const string MessagesUrl = "https://api.pushover.net/1/messages.json";

    public static async Task<PushoverAlertResult> SendAsync(
        HttpClient httpClient,
        string? appToken,
        string? userKey,
        string message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appToken) || string.IsNullOrWhiteSpace(userKey))
        {
            return new PushoverAlertResult(PushoverAlertOutcome.NotConfigured);
        }

        try
        {
            var payload = new Dictionary<string, string>
            {
                ["token"] = appToken,
                ["user"] = userKey,
                ["message"] = message
            };

            using var content = new FormUrlEncodedContent(payload);
            var response = await httpClient.PostAsync(MessagesUrl, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new PushoverAlertResult(PushoverAlertOutcome.HttpFailure, $"HTTP {(int)response.StatusCode}: {body}");
            }

            return new PushoverAlertResult(PushoverAlertOutcome.Sent);
        }
        catch (Exception ex)
        {
            return new PushoverAlertResult(PushoverAlertOutcome.Exception, ex.Message);
        }
    }
}

public enum PushoverAlertOutcome
{
    NotConfigured,
    Sent,
    HttpFailure,
    Exception
}

public sealed record PushoverAlertResult(PushoverAlertOutcome Outcome, string? Detail = null);
