namespace Drps.Shared.BuildCurrency;

/// <summary>
/// Three-way outcome, not two - "stale" and "couldn't tell" are deliberately distinct statuses,
/// never collapsed into one. A check that can't reach git must never be reported the same way
/// as a check that ran cleanly and found nothing stale - fail-open means "assume current, log a
/// quiet warning, keep running," not "assume stale" or "assume current" without saying which.
/// </summary>
public enum BuildCurrencyStatus
{
    Current,
    Stale,
    Unverifiable
}

/// <summary>
/// Result of BuildCurrencyChecker.CheckAsync. StaleCommitCount/ChangedPaths are only meaningful
/// when Status is Stale; Detail is only meaningful when Status is Unverifiable - both left at
/// their defaults otherwise rather than modeled as separate result types, since callers only
/// ever branch on Status first (see Program.cs's own switch at the call site).
/// </summary>
public sealed record BuildCurrencyCheckResult(
    BuildCurrencyStatus Status,
    int StaleCommitCount = 0,
    IReadOnlyList<string>? ChangedPaths = null,
    string? Detail = null)
{
    public static BuildCurrencyCheckResult Current() => new(BuildCurrencyStatus.Current);

    public static BuildCurrencyCheckResult Stale(int staleCommitCount, IReadOnlyList<string> changedPaths) =>
        new(BuildCurrencyStatus.Stale, staleCommitCount, changedPaths);

    public static BuildCurrencyCheckResult Unverifiable(string detail) =>
        new(BuildCurrencyStatus.Unverifiable, Detail: detail);
}
