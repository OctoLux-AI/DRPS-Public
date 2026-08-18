using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// One-time, self-clearing skip for a ticker's next OPEN fire attempt after an
/// OrderFiringOutcome.AmbiguousUnresolved outcome (CLAUDE.md's 2026-07-31 retry-ambiguity audit,
/// Gap 1 design decision). Deliberately NOT the same mechanism as ExcludedTicker - that table is
/// for a real Alpaca/Ledger orphan that cannot be auto-healed and requires manual clearing;
/// this one exists to close a narrow, low-severity double-fire window (client_order_id
/// determinism breaks if Gate re-scans and produces a new GateScoreId/AdjusterAllocationId pair
/// for the same ticker before an ambiguous attempt is resolved) and clears itself automatically
/// the very next time it's checked - no human action, no cooldown, no retry counter.
///
/// Written whenever FireAsync or FireCloseAsync resolves to AmbiguousUnresolved for a ticker
/// (OrderFiringService.NotifyOutcomeAsync) - both directions record a row, for a complete audit
/// trail, even though only the OPEN path (OrchestrationWorker.ProcessOpenCandidateAsync) actually
/// reads and consumes one today. The close side's own client_order_id
/// ("drps-close-{PositionId}-{attempt}") is keyed on the stable Position.Id, not a
/// GateScoreId/AdjusterAllocationId pair that can be superseded by a re-scan, so the specific
/// vulnerability this guard exists for does not apply there - see the CLAUDE.md decision block
/// for the full reasoning.
///
/// Mutable tracker, not append-only - same category as SourceStatus/KillSwitchCounter/
/// ConsecutiveLossCircuitBreaker: a row is created once and updated in place (ConsumedAt set)
/// rather than superseded by a new row.
/// </summary>
public class AmbiguousFireSkip
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Ticker { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Null until the very next open-candidate check for this ticker consumes it - at which
    // point that one candidate is skipped (not fired) and this is stamped, permanently spending
    // the skip. Never cleared/reset by anything else - a fresh AmbiguousUnresolved outcome
    // writes a brand new row rather than reusing an already-consumed one.
    public DateTimeOffset? ConsumedAt { get; set; }
}
