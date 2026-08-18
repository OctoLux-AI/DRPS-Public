namespace Drps.Adjuster.Configuration;

// Adjuster: Multi-Signal Multiplier Combination - Weighted-Deviation-Sum Model (CLAUDE.md
// 2026-07-26). Day-one weights are both 1.0 (equivalent to pure additive combination, no
// diminishing-returns behavior yet) - the locked decision's own stated starting point, not a
// guess made here. Bound from configuration (section "MultiSignalWeights" via
// IOptions<MultiSignalWeightOptions>) so these can be retuned later (e.g. decaying weights by
// stack position once a third, regime signal exists) without a code change, once real
// Monolith/AAR data justifies trusting one signal's deviation more than another's - same "not a
// magic number buried in code" instruction as SentimentMultiplierOptions.
public class MultiSignalWeightOptions
{
    public decimal InsiderWeight { get; set; } = 1.0m;
    public decimal SentimentWeight { get; set; } = 1.0m;

    // Options-flow (put/call volume ratio) signal weight - same day-one 1.0 default as
    // InsiderWeight/SentimentWeight above. Not yet consumed by MultiSignalMultiplierCombiner's
    // real call site (OrderFiringService) - OptionsFlowMultiplierService itself is standalone
    // and unwired as of this addition; wiring both together is a separate, later task.
    public decimal OptionsWeight { get; set; } = 1.0m;
}
