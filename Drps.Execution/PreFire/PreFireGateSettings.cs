namespace Drps.Execution.PreFire;

// Bound from configuration section "PreFireGate". Config-driven placeholder (CLAUDE.md's
// Execution Layer: First Design Decision - "5/day, stated placeholder... revisit once real
// trade volume exists to judge against"), never hardcoded as a literal inside
// PreFireGateService/KillSwitchTracker.
public class PreFireGateSettings
{
    public int KillSwitchMaxOpensPerDay { get; set; } = 5;
}
