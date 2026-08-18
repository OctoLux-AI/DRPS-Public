using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Computed RVOL (relative volume) value for one symbol/date, tagged with a calculation-
/// version identifier per the Immutability & Extensibility rule. Append-only: a formula
/// change adds a new versioned row set, it never rewrites an existing one. Same Value +
/// Provenance shape and HasExDividendEvent annotation convention as DmaIndicator/
/// RsiIndicator - no cached Verified bit here either; verification is resolved live, at
/// read time, by RvolVerificationJoinService. See RvolExDividendAnnotator's own doc comment
/// for why HasExDividendEvent means something different here than it does for the
/// price-based indicators, even though the mechanism is identical.
/// </summary>
public class RvolIndicator
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Symbol { get; set; }

    public DateOnly BarDate { get; set; }

    // Always 20 today (the trailing baseline window RVOL averages against) - carried as a
    // real column rather than a hardcoded assumption, so a future different baseline size
    // doesn't need a schema change.
    public int BaselineWindow { get; set; }

    public decimal Value { get; set; }

    public bool HasExDividendEvent { get; set; }

    // True when any date within this row's lookback window had its Close corrected via
    // BarVerification.PrimarySourceValue (Tiingo) - see
    // RvolComputationService.GetTiingoCorrectedClosesAsync's doc comment (CLAUDE.md,
    // "Reconciliation: Narrow Tiingo-Close Exception for the OHL-Agrees/C-Disagrees
    // Signature", 2026-07-17). RVOL's own Value is Volume-based and is never itself
    // substituted - this flag is purely informational context, same category as
    // HasExDividendEvent's own RVOL-specific reasoning.
    public bool HasTiingoCorrectedClose { get; set; }

    // CLAUDE.md's "Calculator Ticker Source: Watchlist + Discovered-Aligned Union, Not a
    // Swap" (2026-07-30) - same field, same provenance intent, and same required-no-default
    // convention as DmaIndicator.TickerSourceOrigin. Gate's composite score requires RVOL
    // (and RSI), so a discovered-aligned ticker with a DmaIndicator row but no RvolIndicator
    // row could never actually be scored - this closes that gap.
    public TickerSourceOrigin TickerSourceOrigin { get; set; }

    public int CalculationVersion { get; set; }

    public DateTimeOffset ComputedAt { get; set; }
}
