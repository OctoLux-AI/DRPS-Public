namespace Drps.Shared.Exceptions;

// Thrown by a feeder before any HTTP call is attempted, when a required config value
// (API key/secret) is null, empty, or whitespace. Distinct from a real source-side
// rejection (e.g. a genuine 401) so orchestration can avoid counting a client-side
// configuration bug as a strike against that source's trust state.
public class ConfigurationMissingException : Exception
{
    public string ConfigKey { get; }

    public ConfigurationMissingException(string configKey)
        : base($"Required configuration value '{configKey}' is missing or empty.")
    {
        ConfigKey = configKey;
    }
}
