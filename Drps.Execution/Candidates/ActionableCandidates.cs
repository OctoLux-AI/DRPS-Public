namespace Drps.Execution.Candidates;

// Deliberately two separate lists rather than one polymorphic "action" collection - firing an
// open needs a GateScore+AdjusterAllocation pair (OrderFiringService.FireAsync's shape), firing
// a close needs only a Position (OrderFiringService.FireCloseAsync's shape). Collapsing both
// into one generic type would either lose that distinction or force every consumer to
// pattern-match/downcast to recover it.
public class ActionableCandidates
{
    public required IReadOnlyList<OpenCandidate> OpenActions { get; init; }

    // Already deduplicated by Position.Id - see CandidateOrchestrator.GetActionableCandidatesAsync
    // for the dedup mechanism. Each CloseAction carries forward which triggering query(s)
    // actually flagged it (CLAUDE.md's Execution Layer: Candidate Orchestrator Correction) -
    // a bare Position here would silently discard that provenance, including the overlap case
    // where both queries flag the same position at once.
    public required IReadOnlyList<CloseAction> CloseActions { get; init; }
}
