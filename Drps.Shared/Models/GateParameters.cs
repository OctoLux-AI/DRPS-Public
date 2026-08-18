using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Drps.Shared.Models;

/// <summary>
/// Versioned, append-only table of Gate's tunable numeric thresholds - the RSI/RVOL
/// quality-curve anchors, composite weights, and bucket thresholds GateQualityScorer and
/// GateCompositeService are calibrated against. Every GateScore row tags which GateParameters
/// row (IsActive at scan time) it was scored under, mirroring the existing CalculationVersion
/// discipline on DMA/RSI/RVOL/ATR.
///
/// [REDACTED FOR PUBLIC RELEASE] The property defaults below are placeholders, not DRPS's
/// real shipped tuning - see README.md's "What's intentionally not public" section. They are
/// deliberately kept internally consistent (ordered thresholds, floor &lt; ceiling, etc.) so
/// GateParametersValidator's structural checks and every consumer that depends on this shape
/// still compile and behave sensibly; only the specific calibrated numbers are changed.
/// </summary>
public class GateParameters
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    public DateTime EffectiveFrom { get; set; }

    public bool IsActive { get; set; }

    // RSI quality-curve anchors - a peaked curve: quality RsiFloorQuality at RsiLowerBound,
    // 1.0 at RsiPeak, RsiFloorQuality at RsiUpperBound, linearly interpolated between.
    public decimal RsiLowerBound { get; set; } = 40m;

    public decimal RsiPeak { get; set; } = 50m;

    public decimal RsiUpperBound { get; set; } = 60m;

    // Tier 1 hard-reject floor - a DMA or RSI quality score below this ejects the candidate
    // from scoring entirely.
    public decimal RsiFloorQuality { get; set; } = 0.5m;

    // RVOL quality range - provisionally monotonic.
    public decimal RvolFloorMultiple { get; set; } = 0.5m;

    public decimal RvolCeilingMultiple { get; set; } = 1.5m;

    // RVOL composite weight, full vs. half (unverified RVOL).
    public decimal RvolFullWeight { get; set; } = 0.5m;

    public decimal RvolHalfWeight { get; set; } = 0.25m;

    // RSI's composite weight - RVOL's own weight above already carries its half, so only
    // RSI's weight needs its own named column here.
    public decimal RsiCompositeWeight { get; set; } = 0.5m;

    // Composite-score bucket thresholds - BuyThreshold/WatchThreshold gate new candidacy,
    // ExitThreshold is the lower, deliberately distinct hysteresis floor that closes an
    // already-held position.
    public decimal BuyThreshold { get; set; } = 0.9m;

    public decimal WatchThreshold { get; set; } = 0.6m;

    public decimal ExitThreshold { get; set; } = 0.3m;

    // NoBuy list duration in full trading sessions - a composite-degradation exit excludes
    // the ticker from re-entry until this many full sessions have elapsed.
    public int NoBuySessionCount { get; set; } = 2;
}
