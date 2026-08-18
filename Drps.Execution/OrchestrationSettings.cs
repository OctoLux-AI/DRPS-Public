namespace Drps.Execution;

// Bound from configuration section "Orchestration" via IOptions<OrchestrationSettings> - same
// pattern as Drps.Ingestion's IngestionSettings.
public class OrchestrationSettings
{
    // How often OrchestrationWorker polls CandidateOrchestrator for new actionable candidates.
    // Config-driven placeholder (60s), same "stated, revisable guess" category as every other
    // interval/threshold placeholder in this codebase - not yet calibrated against real trade
    // volume.
    public int PollIntervalSeconds { get; set; } = 60;

    // maxWait passed to FillConfirmationService.ConfirmOpenAsync/ConfirmCloseAsync. A separate
    // knob from PollIntervalSeconds - this bounds one order's own fill-confirmation poll, not
    // how often new candidates are discovered. Placeholder default, same category as above.
    public int FillConfirmationMaxWaitSeconds { get; set; } = 60;

    // Defaults to true deliberately - this must default to safe/off (no real orders placed)
    // rather than safe-on-by-accident requiring someone to remember to disable it before this
    // Worker can ever place a real order. Flipping this to false is the explicit, deliberate
    // "yes, fire real orders" decision - never an accidental side effect of deploying this
    // Worker or adding this config section.
    public bool IsDryRunEnabled { get; set; } = true;
}
