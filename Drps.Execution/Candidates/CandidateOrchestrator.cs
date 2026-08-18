namespace Drps.Execution.Candidates;

/// <summary>
/// Pure decision logic composing the three existing candidate queries (OpenCandidateQuery,
/// CloseCandidateQuery, PlateauDeactivationCandidateQuery) into one result describing what
/// SHOULD happen right now. No hosting, no scheduling, no BackgroundService, and no calls to
/// OrderFiringService/PreFireGateService anywhere in this class - it only says what could be
/// fired, the same "decide, don't act" boundary each underlying query already keeps. A later,
/// separate task wires this output to whatever actually fires orders.
///
/// Deliberately does NOT implement the in-flight-tracking set CLAUDE.md's Execution Layer:
/// Sixth Design Decision describes for the fire-to-fill-confirmation gap - that is stateful,
/// process-lifetime tracking that belongs to the eventual BackgroundService orchestrator, not
/// this stateless query-composition class. Idempotency here is already fully covered by the
/// three underlying queries themselves (no open Position for an open action, an open Position
/// for a close action) - nothing further is needed at this layer.
/// </summary>
public class CandidateOrchestrator
{
    private readonly OpenCandidateQuery _openCandidateQuery;
    private readonly CloseCandidateQuery _closeCandidateQuery;
    private readonly PlateauDeactivationCandidateQuery _plateauDeactivationCandidateQuery;

    public CandidateOrchestrator(
        OpenCandidateQuery openCandidateQuery,
        CloseCandidateQuery closeCandidateQuery,
        PlateauDeactivationCandidateQuery plateauDeactivationCandidateQuery)
    {
        _openCandidateQuery = openCandidateQuery;
        _closeCandidateQuery = closeCandidateQuery;
        _plateauDeactivationCandidateQuery = plateauDeactivationCandidateQuery;
    }

    public async Task<ActionableCandidates> GetActionableCandidatesAsync(CancellationToken cancellationToken)
    {
        var openCandidates = await _openCandidateQuery.GetActionableBuyCandidatesAsync(cancellationToken);
        var closeCandidates = await _closeCandidateQuery.GetActionableCloseCandidatesAsync(cancellationToken);
        var deactivationCandidates = await _plateauDeactivationCandidateQuery.GetActionableDeactivationCandidatesAsync(cancellationToken);

        // Dedup key is Position.Id, not Ticker - CloseCandidateQuery/PlateauDeactivationCandidateQuery
        // both already scope to a currently-open (ExitDate == null) position per their own
        // idempotency rules, so at most one open Position can exist per ticker at any given
        // moment; Position.Id and Ticker are therefore equivalent as a dedup key in practice,
        // but Id is used directly since it is the actual identity CloseCandidateQuery's own
        // pairing is built around (CloseCandidate.Position), not a value derived a second time.
        //
        // A single position can legitimately appear more than once even before considering the
        // two-query overlap: CloseCandidateQuery does not dedupe multiple exit-signal GateScore
        // rows for the same still-held ticker across consecutive scans (each scan that finds the
        // composite score still below ExitThreshold appends another GateBucket.Exit row, per
        // CloseCandidateQuery's own doc comment).
        //
        // CLAUDE.md's Execution Layer: Candidate Orchestrator Correction - the dedup below still
        // collapses to exactly one CloseAction per position, but now preserves BOTH triggering
        // reasons rather than discarding one: a position present in both closeCandidates and
        // deactivationCandidates gets both FromCompositeDegradation and FromPlateauDeactivation
        // set true on its single resulting CloseAction, not silently reduced to whichever source
        // happened to appear first.
        var closeActions = closeCandidates
            .Select(c => (Position: c.Position, FromCompositeDegradation: true, FromPlateauDeactivation: false))
            .Concat(deactivationCandidates.Select(p => (Position: p, FromCompositeDegradation: false, FromPlateauDeactivation: true)))
            .GroupBy(x => x.Position.Id)
            .Select(g => new CloseAction
            {
                Position = g.First().Position,
                FromCompositeDegradation = g.Any(x => x.FromCompositeDegradation),
                FromPlateauDeactivation = g.Any(x => x.FromPlateauDeactivation)
            })
            .ToList();

        return new ActionableCandidates
        {
            OpenActions = openCandidates,
            CloseActions = closeActions
        };
    }
}
