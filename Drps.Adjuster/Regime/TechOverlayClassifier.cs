namespace Drps.Adjuster.Regime;

/// <summary>
/// Tech-overlay sector-bucket classification (CLAUDE.md's Regime: Tech-Overlay Sector Bucket,
/// Empirically Defined, 2026-07-26) - determines whether a candidate's GateScore.Sector value
/// qualifies for the VXN/VIX overlay term in the eventual regime multiplier.
///
/// Classification logic only. This class does not compute the VXN/VIX ratio or any raw
/// multiplier, and is not yet called from MultiSignalMultiplierCombiner or anywhere else in the
/// real combine path - regime has no signal wired into that combiner yet (see its own doc
/// comment). This is a standalone, callable-but-unwired check, per this task's explicit scope.
///
/// Locked, explicit two-value bucket, empirically derived from real Finnhub finnhubIndustry
/// strings already observed in DRPS's own ingested data (the 40-ticker RawSectorObservation
/// audit, 2026-07-23) - not an assumed GICS-style label, and not fuzzy- or partial-matched
/// against related-sounding categories. Per the locked decision, "Technology" and
/// "Semiconductors" are the only two included; "Media" was considered and explicitly excluded
/// (weak connection to Nasdaq-100/VXN specifically), and every other observed Finnhub value is
/// excluded with no ambiguity. This is a snapshot of what's been observed so far in the current
/// watchlist, not a closed taxonomy - CLAUDE.md's own decision flags it for revisit as the
/// watchlist grows and new Finnhub values are observed.
///
/// Case-sensitive, exact match only, deliberately. Finnhub's finnhubIndustry values are already
/// well-formed, consistent strings as ingested - a case-insensitive or fuzzy comparison here
/// would risk silently matching a genuinely different or malformed value rather than the two
/// specific labels this bucket names.
/// </summary>
public static class TechOverlayClassifier
{
    private static readonly HashSet<string> TechOverlaySectors = new(StringComparer.Ordinal)
    {
        "Technology",
        "Semiconductors"
    };

    // Fail-closed: null or any unrecognized sector value resolves to NOT tech-eligible - same
    // posture as every other DRPS classification with missing or unrecognized data.
    public static bool IsTechOverlayEligible(string? sector) =>
        sector is not null && TechOverlaySectors.Contains(sector);
}
