using Drps.Shared.Models;

namespace Drps.Execution.Candidates;

// One deduplicated close action (by Position.Id), carrying forward WHICH triggering
// condition(s) actually flagged it - CLAUDE.md's Execution Layer: Candidate Orchestrator
// Correction. Both flags can be true simultaneously: a position can genuinely satisfy both
// CloseCandidateQuery's composite-degradation signal and PlateauDeactivationCandidateQuery's
// plateau-deactivation signal at once (the two mechanisms are independent code paths over the
// same Position row, per PlateauDeactivationCandidateQuery's own doc comment). That overlap must
// be represented here, not collapsed to a single guessed reason - a downstream consumer
// (OrchestrationWorker) needs the real provenance to pick ExitReason correctly, not a heuristic
// re-derived from Position's own fields after the fact.
//
// Deliberately a different name than CloseCandidate (CloseCandidateQuery's own GateScore+Position
// pairing) - this is the orchestrator's post-dedup output shape, not the same type.
public class CloseAction
{
    public required Position Position { get; init; }
    public required bool FromCompositeDegradation { get; init; }
    public required bool FromPlateauDeactivation { get; init; }
}
