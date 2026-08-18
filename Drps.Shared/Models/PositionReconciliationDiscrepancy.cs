using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// The "both agree it's open, quantity/price disagree" case from CLAUDE.md's Execution Layer:
/// Fifth Design Decision (Ledger/Alpaca Reconciliation) - deliberately NOT logged into the
/// existing OHLCV Discrepancy table, since conflating broker-account drift with bar-level data
/// disagreement would repeat the exact category mistake SectorSourceType was kept separate from
/// SourceType to avoid. Append-only, same philosophy as Discrepancy: each detection is its own
/// row, never updated in place - no Resolved/ResolvedDate field here.
/// </summary>
public class PositionReconciliationDiscrepancy
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    // FK to Position.Id - the Ledger-side row this discrepancy was found against.
    public long PositionId { get; set; }

    [MaxLength(16)]
    public required string Ticker { get; set; }

    public DateTime DetectedDate { get; set; }

    // decimal(18,9) - matches Position.EntryQuantity/ExitQuantity's fractional-share precision.
    public decimal LedgerQuantity { get; set; }

    public decimal AlpacaQuantity { get; set; }

    public decimal LedgerPrice { get; set; }

    // Named to match Alpaca's own field as this codebase's own client already maps it
    // (avg_entry_price -> AlpacaPosition.AverageEntryPrice), not an invented name.
    public decimal AlpacaAverageEntryPrice { get; set; }
}
