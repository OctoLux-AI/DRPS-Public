using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Persisted, per-trading-day counter of order-OPEN attempts (never closes - CLAUDE.md's
/// Execution Layer: First Design Decision). Deliberately mutable, not append-only - same
/// precedent as SourceStatus, which tracks live trust state rather than raw ingested data.
/// Persisted (not in-memory) specifically because the failure mode this exists to catch is a
/// runaway loop that might crash/restart mid-day; an in-memory counter would silently reset
/// at exactly the worst moment.
/// </summary>
public class KillSwitchCounter
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    // One row per trading day - the reset boundary. Deliberately DateOnly, not DateTime: this
    // counter has no time-of-day component, only a day identity.
    public DateOnly TradingDate { get; set; }

    public int OpenAttemptCount { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    // Null until the kill-switch Pushover notification has actually been sent for this trading
    // day's trip. Lives on the same per-TradingDate row as OpenAttemptCount specifically so it
    // resets naturally at the same trading-day boundary - no separate reset mechanism needed
    // (CLAUDE.md's Execution Layer: Ninth Design Decision correction, 2026-07-24 - the original
    // notification fired unconditionally on every EvaluateOpenAsync call while tripped, once per
    // OrchestrationSettings.PollIntervalSeconds poll cycle).
    public DateTimeOffset? NotifiedAt { get; set; }
}
