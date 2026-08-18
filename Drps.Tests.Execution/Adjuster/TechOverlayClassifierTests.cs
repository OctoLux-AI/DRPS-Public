using Drps.Adjuster.Regime;

namespace Drps.Tests.Adjuster;

// CLAUDE.md's Regime: Tech-Overlay Sector Bucket, Empirically Defined (2026-07-26). Locked,
// explicit two-value bucket ("Technology", "Semiconductors") against real Finnhub SectorValue
// strings - classification logic only, not the VXN/VIX overlay computation itself.
public class TechOverlayClassifierTests
{
    [Fact]
    public void IsTechOverlayEligible_Technology_ReturnsTrue()
    {
        Assert.True(TechOverlayClassifier.IsTechOverlayEligible("Technology"));
    }

    [Fact]
    public void IsTechOverlayEligible_Semiconductors_ReturnsTrue()
    {
        Assert.True(TechOverlayClassifier.IsTechOverlayEligible("Semiconductors"));
    }

    [Fact]
    public void IsTechOverlayEligible_Media_ReturnsFalse()
    {
        // Explicitly excluded per the locked decision (DIS, SIRI in current data) - considered
        // and rejected, not merely omitted. Named separately from the generic
        // "unrecognized sector" test below so a future reader can't mistake this for an
        // oversight if "Media" is ever reconsidered without the same scrutiny the decision
        // calls for.
        Assert.False(TechOverlayClassifier.IsTechOverlayEligible("Media"));
    }

    [Fact]
    public void IsTechOverlayEligible_NullSector_ReturnsFalseFailClosed()
    {
        Assert.False(TechOverlayClassifier.IsTechOverlayEligible(null));
    }

    [Fact]
    public void IsTechOverlayEligible_UnrecognizedSectorValue_ReturnsFalse()
    {
        // A made-up value that isn't any real observed Finnhub SectorValue at all - confirms
        // the classifier fails closed on genuinely unknown input, not just on the specific
        // excluded values this file already names.
        Assert.False(TechOverlayClassifier.IsTechOverlayEligible("QuantumWidgets"));
    }

    [Fact]
    public void IsTechOverlayEligible_LowercaseVariant_ReturnsFalseCaseSensitiveByDesign()
    {
        // Deliberately strict, case-sensitive exact match (see TechOverlayClassifier's own doc
        // comment for why) - Finnhub's real finnhubIndustry values are already well-formed,
        // consistent strings, so normalizing case here would risk silently matching a
        // genuinely different or malformed value instead of the two labels this bucket
        // actually names. A lowercase variant is therefore correctly excluded, not a bug.
        Assert.False(TechOverlayClassifier.IsTechOverlayEligible("technology"));
    }

    [Theory]
    [InlineData("Retail")]
    [InlineData("Energy")]
    [InlineData("Banking")]
    [InlineData("Pharmaceuticals")]
    [InlineData("Aerospace & Defense")]
    [InlineData("Beverages")]
    [InlineData("Financial Services")]
    [InlineData("Hotels, Restaurants & Leisure")]
    [InlineData("Biotechnology")]
    [InlineData("Health Care")]
    [InlineData("Industrial Conglomerates")]
    [InlineData("Logistics & Transportation")]
    [InlineData("Machinery")]
    public void IsTechOverlayEligible_OtherObservedFinnhubValues_AllReturnFalse(string sector)
    {
        // Every other Finnhub SectorValue actually observed in DRPS's current 40-ticker
        // watchlist (per the audit the locked decision cites) - none are ambiguous, all
        // correctly excluded.
        Assert.False(TechOverlayClassifier.IsTechOverlayEligible(sector));
    }
}
