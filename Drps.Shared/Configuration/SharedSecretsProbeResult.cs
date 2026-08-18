namespace Drps.Shared.Configuration;

/// <summary>
/// Three-way outcome, matching BuildCurrencyCheckResult's own precedent (this codebase's
/// "Current/Stale/Unverifiable" shape) - "the file wasn't there" and "the file was there but
/// broken" are deliberately distinct statuses, never collapsed into one, since only the latter
/// is worth a loud log line.
/// </summary>
public enum SharedSecretsProbeStatus
{
    NotFound,
    Loadable,
    FailedToLoad
}

/// <summary>
/// Result of SharedSecretsProbe.Probe. FailureReason is only meaningful when Status is
/// FailedToLoad.
/// </summary>
public sealed record SharedSecretsProbeResult(SharedSecretsProbeStatus Status, string? FailureReason = null)
{
    public static SharedSecretsProbeResult NotFound() => new(SharedSecretsProbeStatus.NotFound);

    public static SharedSecretsProbeResult Loadable() => new(SharedSecretsProbeStatus.Loadable);

    public static SharedSecretsProbeResult FailedToLoad(string reason) =>
        new(SharedSecretsProbeStatus.FailedToLoad, reason);
}
