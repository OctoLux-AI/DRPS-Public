using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Computed RSI concavity (second discrete difference: RsiConcavity[t] = RsiSlope[t] -
/// RsiSlope[t-1]) for one symbol/date, tagged with a calculation-version identifier per the
/// Immutability &amp; Extensibility rule - same Value + Provenance shape as RsiSlopeIndicator.
/// Watch-only per CLAUDE.md's "RsiSlope / RsiConcavity: Design Direction Locked" (2026-07-31):
/// computed and persisted here, NOT wired into any consumer yet - concavity has no confirmed
/// downstream use, it exists to accumulate real data for a future decision.
///
/// <see cref="ConfirmedDirection"/> uses its own, longer/stricter confirmation-streak length
/// than RsiSlopeIndicator's - see RsiConcavityConfirmationEvaluator's own doc comment for why
/// (differencing an already-differenced value compounds noise).
///
/// <see cref="SlopeLookback"/> records the "n" that produced the underlying RsiSlopeIndicator
/// series this row was derived from - informational traceability only, same "Period"-column
/// precedent as every other indicator in this codebase; concavity's own step between
/// consecutive slope readings is always 1, so there is no separate lookback of its own to
/// record.
///
/// <see cref="VerificationScopeLimited"/> is always true, inherited transitively from
/// RsiIndicator's own permanent disclaimer via RsiSlopeIndicator - concavity is derived from
/// slope, which is derived from RSI, so it cannot be narrower in scope than either input.
/// </summary>
public class RsiConcavityIndicator
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [MaxLength(16)]
    public required string Symbol { get; set; }

    public DateOnly BarDate { get; set; }

    // The RsiSlopeIndicator.Lookback value of the slope series this concavity row was derived
    // from - see this class's own doc comment.
    public int SlopeLookback { get; set; }

    public decimal Value { get; set; }

    public SlopeConfirmationDirection ConfirmedDirection { get; set; }

    // Aggregated (OR) from the two underlying RsiSlopeIndicator rows (BarDate and its immediate
    // predecessor slope reading) this concavity value was computed from.
    public bool HasExDividendEvent { get; set; }

    public bool HasTiingoCorrectedClose { get; set; }

    // Always true - see this class's own doc comment.
    public bool VerificationScopeLimited { get; set; } = true;

    public int CalculationVersion { get; set; }

    public DateTimeOffset ComputedAt { get; set; }
}
