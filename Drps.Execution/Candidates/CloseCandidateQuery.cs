using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Execution.Candidates;

/// <summary>
/// Read-only candidate finder mirroring OpenCandidateQuery's shape for the exit side -
/// identifies which Gate-driven exit signals are actually actionable right now. No side
/// effects, no writes, no calls to PreFireGateService/OrderFiringService - this class only
/// answers "what could be closed," never closes anything itself.
///
/// GateBucket.Exit, confirmed directly from GateCompositeService.DetermineBucket, is the ONLY
/// GateScore.Bucket value that represents a Gate-driven exit signal - it fires exactly when an
/// already-held position's composite score drops below GateParameters.ExitThreshold
/// (composite-degradation exit, Gate: Seventh Design Decision). This is genuinely the entire
/// set of exit signals GateScore.Bucket can carry - Buy/Watch/Neutral never represent an exit.
///
/// Plateau deactivation and sector displacement, both named in CLAUDE.md's Gate design
/// decisions as exit-worthy conditions, do NOT flow through GateScore.Bucket at all and are
/// deliberately out of scope for this class as a result, not an oversight:
/// - Plateau deactivation is stamped directly onto Position.DeactivatedDate via
///   LedgerLifecycleStampService.StampPlateauReassessmentAsync (GateScanService's own plateau
///   reassessment loop) - confirmed no GateScore row is created or touched by that path at all.
/// - Sector displacement exists only as a PositionExitReason enum value for manual Ledger CLI
///   recording (Position.cs) - no automated code anywhere detects or signals this condition
///   today; CLAUDE.md's own Adjuster: Ninth/Eleventh Decision blocks leave the mechanism
///   unresolved. There is nothing for a GateScore-based query to find here yet.
/// A future task that wants to surface plateau-deactivated positions as closeable would need
/// to query Position.DeactivatedDate directly - a different data source, not an extension of
/// this class.
///
/// The ATR ratchet stop is explicitly excluded per the same reasoning already established for
/// OpenCandidateQuery's scope: CLAUDE.md's Execution Layer: Third Design Decision places ATR-stop
/// monitoring on its own independent live-polling mechanism, entirely outside GateScore's scan
/// cadence - this class never attempts to detect it.
/// </summary>
public class CloseCandidateQuery
{
    private readonly DrpsDbContext _dbContext;

    public CloseCandidateQuery(DrpsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<List<CloseCandidate>> GetActionableCloseCandidatesAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.GateScores
            .Where(g => g.Bucket == GateBucket.Exit)
            .Join(
                _dbContext.Positions.Where(p => p.ExitDate == null),
                g => g.Ticker,
                p => p.Ticker,
                (g, p) => new CloseCandidate { GateScore = g, Position = p })
            .ToListAsync(cancellationToken);
    }
}
