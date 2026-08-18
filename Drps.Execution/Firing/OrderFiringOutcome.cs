namespace Drps.Execution.Firing;

public enum OrderFiringOutcome
{
    Fired,
    RejectedByPreFireGate,
    SkippedZeroQuantity,
    QuoteFetchFailed,
    RejectedByBroker,

    // Timeout/5xx, checked via GetOrderByClientOrderIdAsync, not found, retried exactly once,
    // and the retry was ALSO ambiguous. Never retried a second time (CLAUDE.md's Execution
    // Layer: Second Design Decision) - this is a genuine "cannot confirm either way" outcome
    // that needs manual review, not a guess in either direction.
    AmbiguousUnresolved
}
