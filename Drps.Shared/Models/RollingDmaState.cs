using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Persisted, mutable per-(Ticker, Term) DMA rolling-sum state - the full-universe cheap
/// pre-filter's "sticky state" (CLAUDE.md's "Rolling DMA State Machine", 2026-07-22), NOT a
/// replacement for the append-only, dual-verified DmaIndicator table Gate's Tier 1 gate reads.
/// Same mutable-tracker category as SourceStatus/KillSwitchCounter - this row IS the state, not
/// raw ingested data, so it is updated in place rather than appended.
///
/// One row per (Ticker, Term, CalculationVersion). RollingDmaStateService updates all four
/// terms (5/15/30/60) for a ticker together every processed bar, using O(1) incremental
/// drop-oldest/add-newest math once BarCount reaches Term, falling back to a bounded O(w)
/// from-scratch recompute only on bootstrap, on a detected trading-calendar gap, or on the
/// fixed 30-night re-anchor cadence - never a full recompute every night, the CapitalFill
/// anti-pattern this component exists to avoid.
/// </summary>
public class RollingDmaState
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Ticker { get; set; }

    // 5, 15, 30, or 60 - matches DmaCalculator.Windows.
    public int Term { get; set; }

    // The raw rolling sum of the trailing (up to Term) closes - NOT the average. Value below
    // is RollingSum / BarCount, computed once BarCount reaches Term.
    public decimal RollingSum { get; set; }

    // Bars folded into RollingSum so far. Grows by one per processed bar while below Term
    // (window still filling); once it reaches Term it stays pinned there forever after - from
    // that point every update drops exactly one bar for every one it adds.
    public int BarCount { get; set; }

    // RollingSum / Term, only meaningful once BarCount >= Term. Null while InsufficientHistory
    // is true - a partial-window average is never silently computed and exposed as if it were
    // a real DMA-Term value.
    public decimal? Value { get; set; }

    // True while BarCount < Term (a new listing, or a ticker still filling its trailing
    // window after re-entry bootstrap) - explicit flag per CLAUDE.md's "insufficient history
    // gets an explicit verification-scope flag, not a silently-computed partial average," not
    // the same concept as RsiIndicator/AtrIndicator's own VerificationScopeLimited (that one is
    // about Wilder-recurrence's unbounded lookback tail, a different limitation entirely) -
    // named distinctly here to avoid conflating the two.
    public bool InsufficientHistory { get; set; }

    // Most recent trading date folded into RollingSum - the anchor incremental updates advance
    // from, and the basis for trading-calendar gap detection on the next run.
    public DateOnly LastBarDate { get; set; }

    // Sticky per-term alignment flag. For the Term=5/15/30 rows, this is that term's own
    // pairwise comparison (DMA5&gt;DMA15, DMA15&gt;DMA30, DMA30&gt;DMA60 respectively). For the
    // Term=60 row specifically, this is NOT an independent "is DMA-60 aligned" concept - see
    // DmaAlignmentTerm.FullStackAligned's own doc comment: Gate's authoritative IsDmaAligned
    // formula has no standalone conjunct for the 60-day window, so this flag is instead the AND
    // of the other three rows' flags, mathematically identical to (not divergent from) Gate's
    // combined IsDmaAligned check. It can never flip independently of, or precede, the other
    // three terms. Compared against on every update to detect a flip and emit a
    // DmaCrossingLogEntry row.
    public bool IsAligned { get; set; }

    // Nights since the last full from-scratch recompute (bootstrap or re-anchor both reset
    // this to 0). At 30, the next update re-anchors instead of incrementing - CLAUDE.md's
    // "Rolling DMA State Machine - Cadence Decisions Locked" fixed 30-night interval.
    public int UpdatesSinceAnchor { get; set; }

    // Bump when the incremental formula itself changes - same Immutability & Extensibility
    // convention as DmaIndicator.CalculationVersion, adapted for a mutable tracker: a version
    // bump starts a fresh (Ticker, Term, CalculationVersion) row from a clean bootstrap rather
    // than mutating an existing row under new rules.
    public int CalculationVersion { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
