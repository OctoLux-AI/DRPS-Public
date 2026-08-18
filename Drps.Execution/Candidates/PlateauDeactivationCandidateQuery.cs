using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Execution.Candidates;

/// <summary>
/// Read-only candidate finder for the third exit-worthy condition CloseCandidateQuery's own
/// doc comment explicitly flags as out of its scope: plateau deactivation.
/// LedgerLifecycleStampService.StampPlateauReassessmentAsync already stamps
/// Position.DeactivatedDate directly when GateScanService's plateau reassessment loop
/// determines a held position is Inadequate - no GateScore row is ever created or touched by
/// that path (confirmed directly, same finding CloseCandidateQuery's doc comment records).
/// There is therefore no GateScore to pair with here at all, unlike OpenCandidate/CloseCandidate
/// - a plain Position result is the honest shape, not a single-field wrapper invented for
/// consistency's sake alone.
///
/// No side effects, no writes, no calls to PreFireGateService/OrderFiringService - this class
/// only answers "which already-deactivated positions still need to be closed," never closes
/// anything itself.
///
/// Deliberately does NOT deduplicate against CloseCandidateQuery's results. A position can
/// genuinely satisfy both queries at once - e.g. a composite-degradation exit signal (Gate scan)
/// and a plateau deactivation (a separate reassessment pass, same or a nearby scan cycle) can
/// both apply to the same held ticker around the same time, since the two mechanisms are
/// entirely independent code paths over the same Position row. Reconciling that overlap (e.g.
/// deciding not to fire two redundant close attempts for one ticker) is the orchestrator's job,
/// not this query's - it only reports what CLAUDE.md's own data says is true.
/// </summary>
public class PlateauDeactivationCandidateQuery
{
    private readonly DrpsDbContext _dbContext;

    public PlateauDeactivationCandidateQuery(DrpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<Position>> GetActionableDeactivationCandidatesAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Positions
            .Where(p => p.DeactivatedDate != null && p.ExitDate == null)
            .ToListAsync(cancellationToken);
    }
}
