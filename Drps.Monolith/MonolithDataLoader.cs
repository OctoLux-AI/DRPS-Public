using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Monolith;

/// <summary>
/// Thin DB-reading seam between DrpsDbContext and Drps.Monolith's DB-free replay/grading logic
/// (ReplayJoinService/GradingService/GradingReportBuilder) - applies SyntheticDataExclusions'
/// hard filter at load time, so no synthetic row ever reaches the join/grading layer, rather
/// than relying on every downstream consumer to remember to filter it out itself.
/// </summary>
public class MonolithDataLoader
{
    private readonly DrpsDbContext _dbContext;

    public MonolithDataLoader(DrpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<GateScore>> LoadRealGateScoresAsync(CancellationToken cancellationToken)
    {
        var excludedIds = SyntheticDataExclusions.SyntheticGateScoreIds;
        return await _dbContext.GateScores
            .Where(g => !excludedIds.Contains(g.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Position>> LoadRealPositionsAsync(CancellationToken cancellationToken)
    {
        var excludedIds = SyntheticDataExclusions.SyntheticPositionIds;
        return await _dbContext.Positions
            .Where(p => !excludedIds.Contains(p.Id))
            .ToListAsync(cancellationToken);
    }
}
