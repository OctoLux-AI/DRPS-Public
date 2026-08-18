namespace Drps.Execution.PreFire;

// Input to PreFireGateService.EvaluateOpenAsync - an order-OPEN attempt only (the kill switch
// never counts closes, per CLAUDE.md's Execution Layer: First Design Decision). No AsOf field
// here deliberately - the as-of timestamp comes from PreFireGateService's own injected clock
// seam, same pattern GateScanService/AdjusterScanService already establish, never from a
// caller-supplied value.
public class PreFireOpenRequest
{
    public required string Symbol { get; init; }

    // Estimated total dollar cost of the proposed order - what the cash-floor check measures
    // against live buying power. Computing this value (marketable-limit pricing, share count)
    // is the future fire-mechanism task's job, not this service's.
    public required decimal ProposedDollarAmount { get; init; }
}
