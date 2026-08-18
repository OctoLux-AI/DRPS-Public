using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Per-ticker exclusion from new automated action - CLAUDE.md's Execution Layer: Fifth Design
/// Decision's orphan-position handling (an Alpaca ∖ Ledger real position that cannot be
/// auto-healed is excluded per-ticker, not resolved by halting the whole system). A ticker
/// either is or isn't excluded - Ticker is unique, not a repeatable log entry like Discrepancy/
/// PositionReconciliationDiscrepancy. No ResolvedDate or removal mechanism yet - clearing an
/// exclusion is a manual-intervention question left open for a later task, not decided here.
/// </summary>
public class ExcludedTicker
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Ticker { get; set; }

    public required string Reason { get; set; }

    public DateTime CreatedDate { get; set; }
}
