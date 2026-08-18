using Microsoft.Extensions.Options;

namespace Drps.Execution.Notifications;

/// <summary>
/// Real implementation of IPushoverNotificationService - POSTs to Pushover's message API
/// (https://api.pushover.net/1/messages.json, confirmed directly against Pushover's own API
/// docs at https://pushover.net/api before writing this, not assumed): POST,
/// application/x-www-form-urlencoded, three required fields (token, user, message). A
/// successful call returns HTTP 200 with {"status":1,...}; this method only needs
/// response.IsSuccessStatusCode, not the response body's own status field, since Pushover
/// returns a non-2xx HTTP status for every rejected request (per that same API doc).
///
/// A blank/placeholder AppToken or UserKey is not an error - the notification is simply
/// skipped, logged at Information (the would-be message is included in that log line, so
/// nothing is silently lost even when Pushover isn't configured yet). Any exception or
/// non-success HTTP response is caught and logged internally; this method never throws, per
/// IPushoverNotificationService's own contract - a Pushover outage must never be the reason an
/// order-firing/reconciliation/pre-fire-gate call fails.
/// </summary>
public class PushoverNotificationService : IPushoverNotificationService
{
    public const string HttpClientName = "PushoverNotificationClient";

    private const string MessagesPath = "/1/messages.json";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PushoverOptions _options;
    private readonly ILogger<PushoverNotificationService> _logger;

    public PushoverNotificationService(
        IHttpClientFactory httpClientFactory, IOptions<PushoverOptions> options, ILogger<PushoverNotificationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AppToken) || string.IsNullOrWhiteSpace(_options.UserKey))
        {
            _logger.LogInformation(
                "[PUSHOVER]: notification skipped - Pushover:AppToken/Pushover:UserKey not configured. Message would have been: {Message}",
                message);
            return;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);

            var payload = new Dictionary<string, string>
            {
                ["token"] = _options.AppToken,
                ["user"] = _options.UserKey,
                ["message"] = message
            };

            using var content = new FormUrlEncodedContent(payload);
            var response = await client.PostAsync(MessagesPath, content, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "[PUSHOVER]: notification failed - HTTP {StatusCode}: {Body}. Message was: {Message}",
                    (int)response.StatusCode, body, message);
            }
        }
        catch (Exception ex)
        {
            // Never propagates - see this class's own doc comment.
            _logger.LogError(ex, "[PUSHOVER]: notification send threw. Message was: {Message}", message);
        }
    }
}
