using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Persisted, single-row circuit-breaker state for consecutive realized-loss closes,
/// account-wide (CLAUDE.md's Execution Layer: Consecutive-Loss Circuit Breaker Design Decision,
/// 2026-07-26). Deliberately mutable, not append-only - same SourceStatus/KillSwitchCounter
/// precedent: this tracks live trip state, not raw historical data. Persisted (not in-memory)
/// for the same reason as KillSwitchCounter - a mid-day crash/restart during an actual losing
/// streak must not silently forget where it was.
///
/// Unlike KillSwitchCounter, there is no calendar-based reset boundary here - Tripped/
/// NotifiedAt/ConsecutiveLossCount are only ever cleared by the manual reset-circuit-breaker
/// CLI action (Drps.Ledger/Program.cs), never automatically. Single row, found-or-created on
/// first use rather than one row per trading day, since this state has nothing to key on.
/// </summary>
public class ConsecutiveLossCircuitBreaker
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public int ConsecutiveLossCount { get; set; }

    public bool Tripped { get; set; }

    // Set once, the first time ConsecutiveLossCount crosses the threshold - not overwritten by
    // further post-trip losses (opens are blocked once tripped, but closes are not, per the
    // design decision's opens-only scope, so further losing closes on already-open positions
    // remain possible while tripped).
    public DateTimeOffset? TrippedAt { get; set; }

    // Null until the Pushover notification for the current trip has actually been sent. Unlike
    // KillSwitchCounter.NotifiedAt (cleared automatically at the next TradingDate row), this is
    // cleared only by the manual reset action - there is no calendar boundary to ride on here.
    public DateTimeOffset? NotifiedAt { get; set; }

    // Guards against double-counting the same close if the circuit-breaker bookkeeping in
    // LedgerPositionWriter.ClosePositionAsync is ever invoked twice for one Position.
    public long? LastEvaluatedPositionId { get; set; }

    // Set by the manual reset-circuit-breaker CLI action; null otherwise.
    public DateTimeOffset? ResetAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
