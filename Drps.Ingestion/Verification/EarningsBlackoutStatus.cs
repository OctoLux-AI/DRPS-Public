namespace Drps.Ingestion.Verification;

// The three states EarningsLookupService's caller must be able to distinguish (Earnings
// Blackout Gate Decision, CLAUDE.md 2026-07-19) - Unverified is deliberately a distinct
// third state, not collapsed into Clear. A candidate Gate has simply never looked at yet
// (no row), a row whose fetch failed, and a row too old to trust must all read the same
// way to a caller applying the fail-closed rule (block on anything that isn't a positively
// confirmed "no blackout"), but they must remain independently visible to a human
// reviewing why a candidate was excluded from BUY - that's exactly what GateScore's
// EarningsDataUnverified flag (set only for this state) exists to preserve downstream.
public enum EarningsBlackoutStatus
{
    // (a) A fresh, verified Finnhub observation exists and the next known earnings date is
    // more than 48 hours away (or no upcoming earnings was found at all).
    Clear,

    // (b) A fresh, verified Finnhub observation exists and the next known earnings date is
    // within 48 hours of the supplied asOf.
    BlackoutActive,

    // (c) No usable observation exists: missing entirely, the most recent fetch failed
    // (Verified = false), or the most recent row is older than the 7-day staleness TTL.
    Unverified
}
