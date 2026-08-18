namespace Drps.Execution.Reconciliation;

/// <summary>
/// Summarizes one ReconciliationService.RunAsync call - counts a future scheduled worker or a
/// human can act on, not just a void return. Success = false only for a top-level failure (the
/// initial Alpaca open-positions fetch itself failing) that aborted the entire run before any
/// orphan/phantom/intersection check could happen - every per-ticker outcome below (an
/// unresolved phantom, an already-known orphan, a logged discrepancy) is a normal, expected
/// result of a run that otherwise completed successfully, not a failure of this type.
/// </summary>
public class ReconciliationResult
{
    public bool Success { get; set; } = true;

    public string? ErrorMessage { get; set; }

    // Orphans (a real Alpaca position with no matching open Ledger row), newly written to
    // ExcludedTicker this run.
    public int NewOrphansDetected { get; set; }

    // Orphans already present on ExcludedTicker from a prior run - not re-inserted (idempotent).
    public int AlreadyKnownOrphans { get; set; }

    // Phantoms (a Position Ledger believed open, with no matching real Alpaca position)
    // confirmed closed via a genuinely filled closing order in Alpaca's own order history, and
    // auto-healed.
    public int PhantomsHealed { get; set; }

    // Phantoms with no genuinely filled closing order found (or whose order-history fetch
    // itself failed) - left open, logged, never guessed at.
    public int PhantomsUnresolved { get; set; }

    // Intersection: both sides agree a position is open, but quantity/price disagree outside
    // tolerance - logged to PositionReconciliationDiscrepancy, Position row never modified.
    public int DiscrepanciesLogged { get; set; }
}
