using Drps.Ingestion.Persistence;
using Drps.Shared.Positioning;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ledger;

/// <summary>
/// Real, Drps.Ledger-backed implementation of IPortfolioStateProvider - reads actual open
/// Position rows rather than StubPortfolioStateProvider's always-zero/empty placeholder. NOT
/// yet wired into Drps.Adjuster's DI registration (a separate task) - StubPortfolioStateProvider
/// remains the active registration until that swap happens.
/// </summary>
public class LedgerPortfolioStateProvider : IPortfolioStateProvider
{
    private readonly DrpsDbContext _dbContext;

    public LedgerPortfolioStateProvider(DrpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PortfolioState> GetCurrentStateAsync(CancellationToken cancellationToken)
    {
        var openPositions = await _dbContext.Positions
            .Where(p => p.ExitDate == null)
            .ToListAsync(cancellationToken);

        var totalDeployedCapital = openPositions.Sum(p => p.EntryPrice * p.EntryQuantity);

        // Looked up separately rather than an inner join against GateScores, so a position's
        // contribution to TotalDeployedCapital never silently depends on its GateScore link
        // resolving - every open position counts toward the total regardless. Also carries
        // CompositeScore now (CLAUDE.md's "Adjuster: Concurrent-Position-Cap Displacement, 10%
        // Relative Composite-Score Margin," 2026-08-01), reusing this same lookup rather than
        // a second query against GateScores.
        var gateScoreIds = openPositions.Select(p => p.GateScoreId).Distinct().ToList();
        var gateScoresById = await _dbContext.GateScores
            .Where(g => gateScoreIds.Contains(g.Id))
            .ToDictionaryAsync(g => g.Id, g => new { g.Sector, g.CompositeScore }, cancellationToken);

        var deployedCapitalBySector = new Dictionary<string, decimal>();
        WeakestHeldPosition? weakestHeldPosition = null;

        foreach (var position in openPositions)
        {
            // An unresolved GateScore link excludes this position from both the sector
            // breakdown and the weakest-held comparison below - its composite score can't be
            // known, so it can't meaningfully participate in either. Still counted in
            // TotalDeployedCapital/OpenPositionCount above/below regardless.
            if (!gateScoresById.TryGetValue(position.GateScoreId, out var gateScore))
            {
                continue;
            }

            // Account-wide, NOT sector-scoped (unlike the sector-breakdown loop just below) -
            // every open position with a resolvable GateScore participates in this comparison
            // regardless of which sector it belongs to, per the locked design decision.
            if (weakestHeldPosition is null || gateScore.CompositeScore < weakestHeldPosition.CompositeScore)
            {
                weakestHeldPosition = new WeakestHeldPosition(position.Ticker, gateScore.CompositeScore);
            }

            // A null (or unresolved) Sector excludes this position from the sector breakdown
            // entirely - matches Gate/Adjuster's existing "null sector means excluded from cap
            // enforcement, not a fake shared bucket" convention. Still counted in
            // TotalDeployedCapital above, and still eligible for the weakest-held comparison
            // above (sector-cap exclusion and position-count-cap exclusion are separate rules).
            if (gateScore.Sector is null)
            {
                continue;
            }

            var amount = position.EntryPrice * position.EntryQuantity;
            deployedCapitalBySector[gateScore.Sector] = deployedCapitalBySector.GetValueOrDefault(gateScore.Sector) + amount;
        }

        return new PortfolioState(
            totalDeployedCapital,
            deployedCapitalBySector,
            OpenPositionCount: openPositions.Count,
            WeakestHeldPosition: weakestHeldPosition);
    }
}
