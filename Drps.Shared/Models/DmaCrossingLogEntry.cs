using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

public enum DmaCrossingDirection
{
    Entered,
    Exited
}

/// <summary>
/// Which alignment condition a DmaCrossingLogEntry/RollingDmaState row represents. Term5,
/// Term15, and Term30 are genuine, independent pairwise comparisons (DMA5&gt;DMA15,
/// DMA15&gt;DMA30, DMA30&gt;DMA60 respectively - RollingDmaAlignmentEvaluator).
///
/// <see cref="FullStackAligned"/> is deliberately NOT named "Term60" - rename, 2026-07-22,
/// following the reconciliation audit that found Gate's own authoritative IsDmaAligned formula
/// (GateScanService.ResolveDmaAsync, Drps.Gate/Scoring/GateScanService.cs:610-612) has no
/// standalone conjunct involving only the 60-day window; DMA60 only ever appears as the third
/// conjunct's right operand. This value is the AND of Term5/Term15/Term30
/// (RollingDmaAlignmentEvaluator.EvaluateAlignment) - mathematically identical to Gate's
/// combined IsDmaAligned, not an independent fourth term. It can never flip on its own, and can
/// never enter alignment before (or without) all three of Term5/Term15/Term30 already having.
///
/// Explicit values (5/15/30/60) are pinned to match DmaCalculator.Windows and
/// RollingDmaState.Term exactly (the same underlying integers, unaffected by this rename - see
/// RollingDmaState.Term's own doc comment) - reordering this enum's declaration cannot silently
/// renumber a stored value. This is why DmaCrossingLogEntryConfiguration stores it as a plain
/// int column rather than following this codebase's usual HasConversion&lt;string&gt;()
/// enum-pinning convention - see that configuration's own comment for the full reasoning.
/// </summary>
public enum DmaAlignmentTerm
{
    Term5 = 5,
    Term15 = 15,
    Term30 = 30,
    FullStackAligned = 60
}

/// <summary>
/// Append-only, dated transition log - the byproduct CLAUDE.md's "Rolling DMA State Machine"
/// section calls "the actually valuable part": one row per (Ticker, Term) whenever that
/// specific term's RollingDmaState.IsAligned flips, rather than one combined per-ticker flag.
/// Tracking Term5/Term15/Term30 independently is what lets a later replay (Drps.Monolith's
/// PositionReplayContext "before" window) see the real progression through the stack - e.g.
/// "ticker X entered Term5-alignment on D, entered Term30-alignment on D+3."
/// FullStackAligned (see <see cref="DmaAlignmentTerm"/>'s own doc comment) is NOT a fourth
/// independent term in that progression - it is the AND of the other three, so it can only ever
/// transition to Entered on the same date as (never before) whichever of Term5/Term15/Term30 is
/// the last to become true.
///
/// Never updated or deleted once written - a correction (e.g. a re-anchor revealing the prior
/// incremental math had drifted) arrives as a new row under a bumped CalculationVersion, same
/// Immutability & Extensibility cornerstone as every other raw/derived table in this codebase.
/// </summary>
public class DmaCrossingLogEntry
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Ticker { get; set; }

    public DmaAlignmentTerm Term { get; set; }

    public DmaCrossingDirection Direction { get; set; }

    // Trading-calendar date the flip was observed on - the date of the bar whose processing
    // caused RollingDmaState.IsAligned to change for this (Ticker, Term).
    public DateOnly TransitionDate { get; set; }

    public int CalculationVersion { get; set; }

    public DateTimeOffset LoggedAt { get; set; }
}
