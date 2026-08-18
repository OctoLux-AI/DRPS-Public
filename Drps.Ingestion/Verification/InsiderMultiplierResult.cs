namespace Drps.Ingestion.Verification;

/// <summary>
/// InsiderLookupService's output for one ticker: the Adjuster sizing multiplier to apply,
/// and whether the underlying data was trustworthy enough to compute a real ratio.
/// Multiplier is always in [1.0, InsiderLookupService.MultiplierCap] - never below 1.0
/// (upgrade-only, per the Insider Form 4 Adjuster Multiplier Decision), never above the cap.
/// </summary>
public sealed record InsiderMultiplierResult
{
    public required decimal Multiplier { get; init; }

    // True only when the multiplier's dollar-volume denominator could not be computed at
    // all (insufficient RawOhlcvBars history) - a genuine data gap. A ticker with real
    // volume history but zero insider purchases in the trailing window is NOT flagged here
    // (Multiplier is still 1.0, but this stays false) - see InsiderLookupService's own doc
    // comment for why that distinction matters.
    public required bool IsDataUnverified { get; init; }
}
