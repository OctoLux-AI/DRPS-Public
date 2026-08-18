namespace Drps.Shared.Positioning;

// CLAUDE.md's "Adjuster: Concurrent-Position-Cap Displacement, 10% Relative Composite-Score
// Margin" (2026-08-01) - the account-wide (never sector-scoped) weakest-composite-score
// currently-held position, if any open position exists. "Weakest" is a pure composite-score
// comparison, the same displacement currency the existing sector-cap precedent already
// established (Gate: Ninth Design Decision) - never capital-weight or velocity.
public record WeakestHeldPosition(string Ticker, decimal CompositeScore);

public record PortfolioState(
    decimal TotalDeployedCapital,
    IReadOnlyDictionary<string, decimal> DeployedCapitalBySector,
    // Both default so every pre-existing caller/test constructing a PortfolioState for
    // sector-cap/reserve-capital scenarios unrelated to position-count displacement is
    // unaffected - same "genuine no-op for a caller that doesn't care" convention already
    // used throughout AdjusterSizingService.ComputeAllocation's own optional parameters.
    // Unlike a persisted entity field (e.g. Position.OpenOrigin), a default here carries none
    // of that field's accidental-non-authoritative-correlate risk: PortfolioState has exactly
    // two real implementations (LedgerPortfolioStateProvider, StubPortfolioStateProvider),
    // both of which supply real, deliberate values from their own method bodies rather than
    // leaving it to chance across many scattered call sites.
    int OpenPositionCount = 0,
    WeakestHeldPosition? WeakestHeldPosition = null);

/// <summary>
/// Seam for querying the portfolio's current deployed-capital state - the real
/// implementation belongs to Drps.Ledger once it exists (same reasoning as
/// IPositionStateProvider, Gate's own seam for exactly this kind of question). Adjuster
/// reads this live to enforce the sector cap and reserve-schedule rules (CLAUDE.md's
/// Adjuster: Sector Cap and Capital Reserve Step Schedule decisions) once Ledger has real
/// position data to report. This interface is only the seam; it is not a Ledger
/// implementation itself.
/// </summary>
public interface IPortfolioStateProvider
{
    Task<PortfolioState> GetCurrentStateAsync(CancellationToken cancellationToken);
}
