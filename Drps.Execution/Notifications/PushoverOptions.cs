namespace Drps.Execution.Notifications;

// Bound from configuration section "Pushover" via IOptions<PushoverOptions>. Property names
// match the shared credentials file (C:\Users\kent\.APIKeys\shared-APIKeys.json)'s
// Pushover:AppToken/Pushover:UserKey keys - that file is the single source of truth across
// projects, not something to rename to match this class (same convention as AlpacaOptions/
// TiingoOptions/FinnhubOptions). Real credentials must never be committed to appsettings.json.
// Missing/blank values are never an error here - PushoverNotificationService checks for that
// itself and simply skips sending, logged, rather than throwing.
public class PushoverOptions
{
    public string AppToken { get; set; } = string.Empty;
    public string UserKey { get; set; } = string.Empty;
}
