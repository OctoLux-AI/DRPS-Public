namespace Drps.Shared.Scheduling;

/// <summary>
/// Parses the "--run-now" on-demand trigger argument, shared across every project's Worker
/// classes so there is exactly one implementation of this parsing rule (CLAUDE.md's "On-Demand
/// Worker Targeting: --run-now Worker-Name Filter + Clean Exit", 2026-07-27). Two independent,
/// mutually exclusive shapes:
///
///  - Bare "--run-now" (no value) - matches ONLY the original per-project Worker class
///    (Drps.Ingestion.Worker / Drps.Calculator.Worker / Drps.Gate.Worker), exactly as it has
///    since "Execution Layer: On-Demand Manual Trigger" (2026-07-24). Never targets a named
///    worker, and the host stays alive afterward (persistent-scheduler mode, unchanged).
///  - "--run-now=&lt;WorkerName&gt;" - targets exactly one worker, matched by exact string
///    equality against that worker's own WorkerRunGuard name (e.g. "Drps.Regime"). A named
///    value that matches no worker in the running process is simply never matched by
///    anything - the process falls through to its normal persistent-scheduler mode for every
///    worker, same as if no flag had been passed at all.
///
/// Whether a matched named run also exits the whole host process once that worker's manual run
/// completes is NOT decided by this class - it is each individual worker's own choice, made in
/// its own ExecuteAsync (corrected 2026-07-28, see CLAUDE.md's "Named On-Demand Worker
/// Targeting: Host-Wide StopApplication() Corrected to Per-Worker Exit"). Drps.Calculator.Worker
/// and Drps.Gate.Worker each still call IHostApplicationLifetime.StopApplication() after their
/// named run, since each is the only BackgroundService in its own process. Drps.Ingestion's
/// seven co-hosted BackgroundServices do NOT - a host-wide stop from any one of them would
/// silently end every sibling's own pending scheduled run (the confirmed 2026-07-27 bug), so a
/// named run there only ends that one worker's own task.
/// </summary>
public static class RunNowArgs
{
    private const string BareFlag = "--run-now";
    private const string NamedPrefix = "--run-now=";

    /// <summary>
    /// True only for the exact bare flag - a named invocation ("--run-now=X") does NOT also
    /// satisfy this, even for the worker whose name happens to be passed. Bare and named are
    /// deliberately mutually exclusive triggers: the original Worker class's unnamed manual-run
    /// behavior is reachable one way only, never accidentally re-triggered by a named flag meant
    /// for a different (or the same) worker.
    /// </summary>
    public static bool IsBareRunNow(string[]? args) =>
        (args ?? Array.Empty<string>()).Contains(BareFlag);

    /// <summary>
    /// True when a named target was supplied via "--run-now=&lt;workerName&gt;" and it matches
    /// <paramref name="workerName"/> exactly (case-sensitive, whole-value match).
    /// </summary>
    public static bool IsNamedRunNow(string[]? args, string workerName) =>
        (args ?? Array.Empty<string>()).Contains(NamedPrefix + workerName);
}
