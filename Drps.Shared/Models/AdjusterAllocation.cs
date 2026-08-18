using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Adjuster's own append-only sizing output for a single GateScore row - kept in a separate
/// table rather than a column on GateScore, since GateScore is Gate's own immutable record
/// and a second writer (Adjuster) touching it after Gate already wrote it would violate the
/// append-only rule. No update paths against this entity - same convention as GateScore.
/// </summary>
public class AdjusterAllocation
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    // FK to GateScore.Id - the scored candidate this allocation was computed for.
    public long GateScoreId { get; set; }

    public decimal AllocationPercent { get; set; }

    public decimal AllocationDollarAmount { get; set; }

    // decimal(18,9) - matches Position.EntryQuantity/ExitQuantity's fractional-share
    // precision (Data Type Discipline). CLAUDE.md's Execution Layer: Third Correction -
    // previously a `long`, which forced AdjusterSizingService to truncate its precisely-
    // computed share quantity to a whole number before this field was ever assigned,
    // silently discarding the fractional remainder OrderFiringService's sub-1-share firing
    // path exists specifically to handle.
    public decimal ShareCount { get; set; }

    public bool ShareCapDeficient { get; set; }

    // As-of timestamp for this sizing computation, sourced from the same injectable clock
    // seam convention as GateScore.ScanDate.
    public DateTime AsOfTimestamp { get; set; }

    // Which tunable Adjuster parameter version was active for this computation.
    public long AdjusterParameterVersion { get; set; }

    // Insider Form 4 Adjuster Multiplier Decision (CLAUDE.md 2026-07-19) - the raw
    // insider-purchase multiplier for this candidate (always in [1.0,
    // InsiderLookupService.MultiplierCap], 1.0 = neutral/no effect), and whether that
    // neutral result reflects a real data gap rather than a genuine "no insider buying"
    // answer. Two separate fields, not one combined value, so a human reviewing allocations
    // can see both how much this affected sizing and whether the underlying data was
    // trustworthy - same "carry provenance separately from the value it describes"
    // precedent as GateScore's own HasTiingoCorrectedData/HasUnverifiedPartialCreditData
    // pair. Per CLAUDE.md's Adjuster: Multi-Signal Multiplier Combination -
    // Weighted-Deviation-Sum Model (2026-07-26), this value is no longer baked into
    // AllocationPercent/AllocationDollarAmount above - it is combined with the sentiment
    // multiplier at fire time (OrderFiringService, via MultiSignalMultiplierCombiner), not
    // applied here at scan time.
    public decimal InsiderMultiplierApplied { get; set; }

    public bool InsiderDataUnverified { get; set; }

    // Options-flow (CBOE delayed-quotes put/call volume ratio) sizing-adjustment multiplier -
    // same "raw signal, recorded here so OrderFiringService's MultiSignalMultiplierCombiner
    // call has it to combine later" role as InsiderMultiplierApplied above, but resolved and
    // persisted directly in AdjusterScanService (no AdjusterSizingService/AllocationResult
    // round-trip - this signal never participated in the sizing math the way insider
    // historically did, so there's no pre-existing parameter list to thread it through).
    // Always in [1.0, OptionsFlowMultiplierOptions.MultiplierCap] by construction
    // (OptionsFlowMultiplierService.ComputeMultiplier's own upgrade-only formula shape) - fail-
    // closed default is exactly 1.0m (neutral), never a bare default(decimal) 0m, which would
    // incorrectly zero out this leg of the combiner entirely rather than leaving it neutral.
    public decimal OptionsFlowMultiplierApplied { get; set; }
}
