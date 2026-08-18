using Drps.Shared.Models;

namespace Drps.Execution.Candidates;

// A BUY-bucket GateScore paired with the AdjusterAllocation OrderFiringService.FireAsync
// already expects to receive together - the exact shape OpenCandidateQuery produces.
public class OpenCandidate
{
    public required GateScore GateScore { get; init; }
    public required AdjusterAllocation AdjusterAllocation { get; init; }
}
