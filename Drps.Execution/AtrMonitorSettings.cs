namespace Drps.Execution;

// Bound from configuration section "AtrMonitor" via IOptions<AtrMonitorSettings> - deliberately
// its own config class, separate from OrchestrationSettings: AtrRatchetMonitorWorker polls at a
// genuinely different cadence for a genuinely different concern (live ATR ratchet stop breach
// detection on already-held positions) than OrchestrationWorker's candidate-discovery poll,
// per CLAUDE.md's Execution Layer: Third Design Decision.
public class AtrMonitorSettings
{
    // How often the ATR ratchet monitor polls every open Position's current price/ATR for a
    // stop breach. Config-driven placeholder (2 minutes) - deliberately separate from
    // OrchestrationSettings.PollIntervalSeconds (60s candidate-discovery cadence). Not yet
    // calibrated against real trade volume, same "stated, revisable guess" category as every
    // other interval placeholder in this codebase.
    public int PollIntervalSeconds { get; set; } = 120;

    // maxWait passed to FillConfirmationService.ConfirmCloseAsync after a genuine breach fires
    // a close order. A separate knob from PollIntervalSeconds - this bounds one order's own
    // fill-confirmation poll, not how often positions are re-checked for a breach. Placeholder
    // default, same category as above.
    public int FillConfirmationMaxWaitSeconds { get; set; } = 60;

    // Defaults to true deliberately, same safe-by-default posture as OrchestrationSettings.
    // IsDryRunEnabled - a real ATR stop breach must not fire a real close order until this is
    // explicitly flipped to false. Never an accidental side effect of deploying this Worker or
    // adding this config section.
    public bool IsDryRunEnabled { get; set; } = true;
}
