using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Computed RSI slope (first discrete difference: RsiSlope[t] = RSI[t] - RSI[t-Lookback]) for
/// one symbol/date, tagged with a calculation-version identifier per the Immutability &amp;
/// Extensibility rule - same Value + Provenance shape as RsiIndicator/DmaIndicator. Watch-only
/// per CLAUDE.md's "RsiSlope / RsiConcavity: Design Direction Locked" (2026-07-31): computed
/// and persisted here, not wired into any Adjuster multiplier or Execution exit-trigger yet.
///
/// <see cref="ConfirmedDirection"/> exists because the locked design requires a slope's sign to
/// hold for 2+ consecutive readings before anything downstream may treat it as a real signal -
/// see RsiSlopeConfirmationEvaluator. Both the raw <see cref="Value"/> and the confirmed flag
/// are persisted; a future consumer needs the confirmed flag, not just the raw number.
///
/// <see cref="Lookback"/> is carried as a real column (config-driven via
/// CalculatorSettings.RsiSlopeLookback, not a hardcoded literal) rather than assumed - same
/// "Period"/"BaselineWindow" precedent as RsiIndicator/RvolIndicator, so a future lookback
/// change doesn't need a schema change and old rows remain honestly labeled with the lookback
/// that actually produced them.
///
/// <see cref="VerificationScopeLimited"/> is always true, inherited from RsiIndicator's own
/// permanent disclaimer (Wilder's RSI has an unbounded decaying dependency chain) - a slope
/// derived from RSI values carries that same scope limitation forward, it cannot be narrower
/// than its own input.
/// </summary>
public class RsiSlopeIndicator
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Symbol { get; set; }

    public DateOnly BarDate { get; set; }

    // The "n" in RsiSlope[t] = RSI[t] - RSI[t-n], as actually configured at compute time - see
    // CalculatorSettings.RsiSlopeLookback.
    public int Lookback { get; set; }

    public decimal Value { get; set; }

    public SlopeConfirmationDirection ConfirmedDirection { get; set; }

    // Aggregated (OR) from every underlying RsiIndicator row in this row's [BarDate - Lookback,
    // BarDate] window - same informational, never-gates-anything category as every other
    // HasExDividendEvent flag in this codebase.
    public bool HasExDividendEvent { get; set; }

    // Aggregated (OR) from every underlying RsiIndicator row in this row's window - true if any
    // of the RSI values this slope was computed from were themselves derived from a
    // Tiingo-corrected Close (CLAUDE.md, 2026-07-17).
    public bool HasTiingoCorrectedClose { get; set; }

    // Always true - see this class's own doc comment. Not a per-row computed condition, same
    // permanent-disclaimer convention as RsiIndicator.VerificationScopeLimited.
    public bool VerificationScopeLimited { get; set; } = true;

    public int CalculationVersion { get; set; }

    public DateTimeOffset ComputedAt { get; set; }
}
