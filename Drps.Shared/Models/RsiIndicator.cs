using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Computed Wilder's-smoothing RSI value for one symbol/date, tagged with a calculation-
/// version identifier per the Immutability & Extensibility rule. Append-only: a formula
/// change adds a new versioned row set, it never rewrites an existing one. Same Value +
/// Provenance shape and HasExDividendEvent annotation convention as DmaIndicator - no
/// cached Verified bit here either; verification is resolved live, at read time, by
/// RsiVerificationJoinService.
///
/// <see cref="VerificationScopeLimited"/> exists because that live verification check is
/// itself bounded: Wilder's smoothing recurrence gives every prior bar in a symbol's entire
/// history a decaying-but-nonzero weight in today's RSI value (see RsiCalculator's own doc
/// comment), but RsiVerificationJoinService only re-checks the most recent 15 bars
/// (Period + 1) - a deliberate, already-accepted practical bound, not a bug. A
/// Verified == true result from that check is scoped to recent history only; older bars'
/// (still-nonzero) contribution to this value is not re-verified. This flag is always true
/// for every row of this indicator type - it is not a per-row computed condition, it's a
/// permanent, honest disclaimer about what kind of indicator this is. DMA and RVOL don't
/// carry this flag: both are memoryless fixed windows with no such unbounded tail.
/// </summary>
public class RsiIndicator
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Symbol { get; set; }

    public DateOnly BarDate { get; set; }

    // Always 14 today (Wilder's standard RSI period) - carried as a real column rather than
    // a hardcoded assumption, so a future different period doesn't need a schema change.
    public int Period { get; set; }

    public decimal Value { get; set; }

    // Same informational data-quality tag as DmaIndicator.HasExDividendEvent - see that
    // entity's own doc comment and CLAUDE.md's Ex-Dividend Handling decision.
    public bool HasExDividendEvent { get; set; }

    // True when any date within this row's lookback window used
    // BarVerification.PrimarySourceValue (Tiingo's Close) in place of Alpaca's raw Close - see
    // RsiComputationService.GetTiingoCorrectedClosesAsync's doc comment for the full scope
    // boundary (CLAUDE.md, "Reconciliation: Narrow Tiingo-Close Exception for the OHL-Agrees/
    // C-Disagrees Signature", 2026-07-17). Informational provenance tag, same category as
    // HasExDividendEvent - never gates or alters anything downstream on its own.
    public bool HasTiingoCorrectedClose { get; set; }

    // Always true - see this class's own doc comment. Not a per-row computed condition;
    // a permanent disclaimer that verification for this indicator type covers only the
    // most recent 15 bars, not Wilder's full (decaying but unbounded) recurrence chain.
    public bool VerificationScopeLimited { get; set; } = true;

    // CLAUDE.md's "Calculator Ticker Source: Watchlist + Discovered-Aligned Union, Not a
    // Swap" (2026-07-30) - same field, same provenance intent, and same required-no-default
    // convention as DmaIndicator.TickerSourceOrigin. Gate's composite score requires RSI (and
    // RVOL), so a discovered-aligned ticker with a DmaIndicator row but no RsiIndicator row
    // could never actually be scored - this closes that gap.
    public TickerSourceOrigin TickerSourceOrigin { get; set; }

    public int CalculationVersion { get; set; }

    public DateTimeOffset ComputedAt { get; set; }
}
