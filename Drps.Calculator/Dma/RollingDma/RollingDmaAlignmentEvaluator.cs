using Drps.Calculator.Dma;
using Drps.Shared.Models;

namespace Drps.Calculator.Dma.RollingDma;

/// <summary>
/// Pure alignment check feeding the crossing log (CLAUDE.md's "Rolling DMA State Machine" and
/// "Crossing-Log Schema - Confirmed" sections, 2026-07-22).
///
/// Reconciliation audit (2026-07-22, later same session): CLAUDE.md names the crossing-log
/// schema but never defines the alignment formula, so this evaluator originally used a
/// self-invented placeholder for the fourth slot (a self-referential slope check against its
/// own prior value). Audited against Drps.Calculator's actual Gate-facing DMA logic - the DMA
/// computation (DmaComputationService/DmaIndicator, Drps.Calculator/Dma/) already feeding real
/// BUY/WATCH/EXIT scoring - and found the authoritative "aligned" definition lives in
/// GateScanService.ResolveDmaAsync (Drps.Gate/Scoring/GateScanService.cs:610-612):
/// <c>isAligned = DMA5 &gt; DMA15 &amp;&amp; DMA15 &gt; DMA30 &amp;&amp; DMA30 &gt; DMA60</c> -
/// a single combined boolean across the full stack, used both as Gate's Tier 1 hard gate
/// (GateQualityScorer.cs:40, <c>if (!input.IsDmaAligned) return Reject(...)</c>) and as a
/// scoring/plateau passthrough (GateCandidateInput.IsDmaAligned, GateScore.IsDmaAligned,
/// GatePlateauEvaluator.IsInadequate). Term5/Term15/Term30 already matched Gate's three
/// individual conjuncts exactly (DMA5&gt;DMA15, DMA15&gt;DMA30, DMA30&gt;DMA60 respectively) -
/// only the fourth slot diverged, since Gate's formula has no standalone conjunct involving only
/// the 60-day window (60 only ever appears as conjunct 3's right operand, never compared
/// against its own prior value).
///
/// Fix: the fourth slot's flag is now the full AND of the other three - i.e. literally Gate's
/// own combined IsDmaAligned value, since that is the only Gate-computed boolean that involves
/// the 60-day window at all. This is not a new interpretation; it removes the invented
/// self-slope signal and replaces it with an exact port of the live, authoritative formula.
///
/// Rename (2026-07-22, later same session, following the reconciliation audit above): the
/// fourth slot was originally exposed and logged as "term 60," a label implying an independent
/// fourth term with its own alignment behavior. It isn't one - it can never lead the other
/// three, never flips independently, and is structurally just "full stack aligned" restated.
/// Renamed to <see cref="DmaAlignmentTerm.FullStackAligned"/> throughout (evaluator output,
/// DmaCrossingLogEntry.Term, RollingDmaStateService, log messages) so a future reader of the
/// crossing log - including a future Monolith replay - doesn't misread "FullStackAligned
/// entered" as a statement about the 60-day moving average specifically. RollingDmaState's own
/// DMA-60 rolling-sum VALUE is untouched by this rename - only the alignment flag's label
/// changed, never the raw computation.
/// </summary>
public static class RollingDmaAlignmentEvaluator
{
    public readonly record struct TermValues(
        decimal? Value5,
        decimal? Value15,
        decimal? Value30,
        decimal? Value60);

    /// <summary>
    /// Returns one boolean per <see cref="DmaAlignmentTerm"/> value. A term whose own
    /// comparison value is unavailable (insufficient history) is never "aligned" - null never
    /// silently reads as true.
    /// <see cref="DmaAlignmentTerm.FullStackAligned"/> is exactly
    /// <c>result[Term5] &amp;&amp; result[Term15] &amp;&amp; result[Term30]</c> - Gate's own
    /// combined IsDmaAligned check, ported verbatim (see this class's own doc comment for the
    /// citation) - not a fourth independent condition.
    /// </summary>
    public static IReadOnlyDictionary<DmaAlignmentTerm, bool> EvaluateAlignment(TermValues values)
    {
        var term5Aligned = values.Value5 is not null && values.Value15 is not null && values.Value5 > values.Value15;
        var term15Aligned = values.Value15 is not null && values.Value30 is not null && values.Value15 > values.Value30;
        var term30Aligned = values.Value30 is not null && values.Value60 is not null && values.Value30 > values.Value60;

        return new Dictionary<DmaAlignmentTerm, bool>
        {
            [DmaAlignmentTerm.Term5] = term5Aligned,
            [DmaAlignmentTerm.Term15] = term15Aligned,
            [DmaAlignmentTerm.Term30] = term30Aligned,
            [DmaAlignmentTerm.FullStackAligned] = term5Aligned && term15Aligned && term30Aligned
        };
    }
}
