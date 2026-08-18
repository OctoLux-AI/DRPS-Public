namespace Drps.Execution.Firing;

public enum FillConfirmationOutcome
{
    // A terminal state was reached with a nonzero fill (fully filled, or partially filled and
    // then canceled/expired - accepted as-is, not chased) and LedgerPositionWriter recorded it.
    Recorded,

    // A terminal state was reached (canceled/expired/rejected) with nothing filled - by
    // design, nothing is written to Ledger for this outcome.
    NoFillRecorded,

    // Still non-terminal after the caller's maxWait elapsed - logged Critical, polling
    // stopped, nothing written. Per CLAUDE.md's Execution Layer: Fourth Design Decision, this
    // is a manual reconciliation case, not something to keep silently waiting on.
    TimedOut,

    // A fill was detected, but the write-back itself failed because LedgerPositionWriter
    // raised a known anomaly (DuplicateOpenPositionException / PositionAlreadyModifiedException)
    // - something else already wrote this position. Logged clearly, not rethrown.
    LedgerWriteAnomaly,

    // Defensive-only: a terminal "filled"/partial-then-settled status was reported, but
    // FilledQuantity/FilledAveragePrice were missing or non-positive. Should not happen against
    // real Alpaca data - logged Critical, nothing written, same "don't guess" instinct as
    // TimedOut.
    AnomalousFillData
}
