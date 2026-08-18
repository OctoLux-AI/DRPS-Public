using Drps.Shared.Models;

namespace Drps.Execution.Candidates;

// A Gate-driven exit-signal GateScore paired with the currently-open Position it applies to.
// Deliberately a different pairing shape than OpenCandidate (GateScore + AdjusterAllocation) -
// a close doesn't need Adjuster's sizing at all, it needs the already-open Position being
// closed.
public class CloseCandidate
{
    public required GateScore GateScore { get; init; }
    public required Position Position { get; init; }
}
