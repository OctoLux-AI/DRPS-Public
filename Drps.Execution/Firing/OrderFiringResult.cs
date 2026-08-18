namespace Drps.Execution.Firing;

public class OrderFiringResult
{
    public required OrderFiringOutcome Outcome { get; init; }

    // Human-readable explanation - null only when Outcome is Fired.
    public string? Reason { get; init; }

    // Populated only when Outcome is Fired - a genuine order ID only exists once Alpaca has
    // confirmed one.
    public string? OrderId { get; init; }

    // Populated for Fired AND for any outcome that reached ToResult's failure branch
    // (RejectedByBroker, AmbiguousUnresolved) - both know the request's own client_order_id
    // regardless of whether Alpaca ever confirmed it. Null only for the earlier-return outcomes
    // that never reach ToResult at all (RejectedByPreFireGate, SkippedZeroQuantity,
    // QuoteFetchFailed). Relied on directly by NotifyOutcomeAsync's AmbiguousUnresolved
    // notification (2026-07-31) to give a human something to grep logs/Alpaca for.
    public string? ClientOrderId { get; init; }
    public decimal? FiredQuantity { get; init; }

    // Non-null only for the whole-share IOC branch when flooring discarded a nonzero
    // remainder - the explicit record this task's own requirement calls for ("do not silently
    // drop it without a record"). Null for the sub-1-share branch (nothing is ever floored
    // there) and null when the whole-share quantity divided evenly.
    public decimal? DiscardedFractionalRemainder { get; init; }

    // Sentiment Adjuster Multiplier Decision (CLAUDE.md 2026-07-24) - populated only by
    // FireAsync's Fired outcomes (open/buy side), so the fired quantity is always traceable
    // back to base size x multiplier, not silently baked into one opaque number. Never null for
    // a FireAsync Fired result, even when the neutral fallback applied (SentimentMultiplier =
    // 1.0m, BaseQuantity = allocation.ShareCount unchanged) - the fields exist specifically to
    // record that the check ran and what it produced, not just to flag a real adjustment.
    // Always null for FireCloseAsync's Fired outcomes: a close has no AdjusterAllocation/base-
    // size concept to adjust (FireCloseAsync's own doc comment explains why sentiment is never
    // called there at all).
    //
    // CLAUDE.md's Adjuster: Multi-Signal Multiplier Combination - Weighted-Deviation-Sum Model
    // (2026-07-26) - FiredQuantity is now BaseQuantity x CombinedMultiplier, not BaseQuantity x
    // SentimentMultiplier directly (SentimentMultiplier is still recorded here as one of the two
    // raw inputs that produced CombinedMultiplier, same traceability reasoning as before).
    public decimal? BaseQuantity { get; init; }
    public decimal? SentimentMultiplier { get; init; }
    public decimal? CombinedMultiplier { get; init; }
}
