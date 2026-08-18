namespace Drps.Shared.BuildCurrency;

/// <summary>
/// Optional, one-shot Pushover alert fired only when BuildCurrencyChecker reports a stale
/// build (CLAUDE.md's "Build-Currency Self-Check" design decision, 2026-08-03, item 5).
/// Deliberately NOT Drps.Execution's IPushoverNotificationService/PushoverNotificationService -
/// Drps.Ingestion/Calculator/Gate reference only Drps.Shared, and shouldn't gain a dependency on
/// Drps.Execution just for a startup-only alert. A raw HttpClient POST instead, same Pushover
/// message-API shape PushoverNotificationService already uses (POST, form-urlencoded,
/// token/user/message - confirmed against Pushover's own API docs when that class was built).
///
/// Never throws - every path (missing credentials, non-2xx HTTP response, any exception)
/// resolves to a BuildCurrencyAlertResult instead, same "a notification failure must never
/// affect [caller] logic" contract PushoverNotificationService's own doc comment states for its
/// near-identical send path. Returns a result rather than logging internally, since Drps.Shared
/// has no existing dependency on Microsoft.Extensions.Logging anywhere else in this project -
/// the caller (already holding its own ILogger in Program.cs) logs the outcome itself.
/// </summary>
public static class BuildCurrencyAlerter
{
    private const string MessagesUrl = "https://api.pushover.net/1/messages.json";

    public static async Task<BuildCurrencyAlertResult> SendStaleBuildAlertAsync(
        HttpClient httpClient,
        string? appToken,
        string? userKey,
        string projectName,
        int staleCommitCount,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(appToken) || string.IsNullOrWhiteSpace(userKey))
        {
            return new BuildCurrencyAlertResult(BuildCurrencyAlertOutcome.NotConfigured);
        }

        try
        {
            var payload = new Dictionary<string, string>
            {
                ["token"] = appToken,
                ["user"] = userKey,
                ["message"] =
                    $"{projectName}: stale build - {staleCommitCount} commit(s) behind main for its own relevant paths. Re-run publish.ps1."
            };

            using var content = new FormUrlEncodedContent(payload);
            var response = await httpClient.PostAsync(MessagesUrl, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return new BuildCurrencyAlertResult(BuildCurrencyAlertOutcome.HttpFailure, $"HTTP {(int)response.StatusCode}: {body}");
            }

            return new BuildCurrencyAlertResult(BuildCurrencyAlertOutcome.Sent);
        }
        catch (Exception ex)
        {
            return new BuildCurrencyAlertResult(BuildCurrencyAlertOutcome.Exception, ex.Message);
        }
    }
}

public enum BuildCurrencyAlertOutcome
{
    NotConfigured,
    Sent,
    HttpFailure,
    Exception
}

public sealed record BuildCurrencyAlertResult(BuildCurrencyAlertOutcome Outcome, string? Detail = null);
