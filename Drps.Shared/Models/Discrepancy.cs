using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// How a logged Discrepancy was handled. <see cref="OhlAgreedCloseResolvedToTiingo"/> is a
/// narrow, evidence-scoped exception (CLAUDE.md, "Reconciliation: Narrow Tiingo-Close
/// Exception for the OHL-Agrees/C-Disagrees Signature", 2026-07-17) — it is NOT a general
/// Alpaca-vs-Tiingo preference. Every other disagreement shape remains
/// <see cref="Unresolved"/> and still follows the standing "Alpaca is primary/ground truth"
/// rule (BarReconciliationService).
/// </summary>
public enum DiscrepancyResolutionMethod
{
    Unresolved,
    OhlAgreedCloseResolvedToTiingo
}

/// <summary>
/// Permanent, append-only log of a source disagreement. Every discrepancy is
/// logged raw, with no deduplication — this is a record of what happened, not
/// a rolling verification state.
/// </summary>
public class Discrepancy
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Symbol { get; set; }

    public DateTimeOffset Timestamp { get; set; }

    public required string FieldOrBarType { get; set; }

    public SourceType SourceA { get; set; }

    public decimal ValueA { get; set; }

    public SourceType SourceB { get; set; }

    public decimal ValueB { get; set; }

    public decimal PercentDiff { get; set; }

    public DateTimeOffset DetectedAt { get; set; }

    public DiscrepancyResolutionMethod ResolutionMethod { get; set; } = DiscrepancyResolutionMethod.Unresolved;
}
