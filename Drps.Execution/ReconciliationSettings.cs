namespace Drps.Execution;

// Bound from configuration section "Reconciliation" via IOptions<ReconciliationSettings> -
// deliberately its own config class, on a genuinely slower, independent cadence from both
// OrchestrationSettings.PollIntervalSeconds (60s candidate-discovery) and
// AtrMonitorSettings.PollIntervalSeconds (120s stop-breach detection), per CLAUDE.md's
// Execution Layer: Fifth Design Decision - drift detection between Drps.Ledger's believed
// state and Alpaca's real account state isn't a sub-minute problem.
public class ReconciliationSettings
{
    // How often ReconciliationWorker runs a full Ledger/Alpaca two-way reconciliation pass.
    // Config-driven placeholder (5 minutes) - same "stated, revisable guess" category as every
    // other interval placeholder in this codebase, not yet calibrated against real trade volume.
    public int PollIntervalSeconds { get; set; } = 300;
}
