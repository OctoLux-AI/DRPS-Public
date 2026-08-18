namespace Drps.Execution.PreFire;

public class PreFireGateResult
{
    public bool Passed { get; init; }

    // Both null when Passed is true. FailedGate identifies which of the four checks rejected
    // the attempt; Reason is a distinct, human-readable explanation - never a generic
    // "rejected" message, per this task's explicit logging requirement.
    public PreFireGate? FailedGate { get; init; }

    public string? Reason { get; init; }
}
