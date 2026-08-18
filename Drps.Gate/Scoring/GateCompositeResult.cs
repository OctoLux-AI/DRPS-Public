using Drps.Shared.Models;

namespace Drps.Gate.Scoring;

/// <summary>
/// GateCompositeService's output for one non-rejected candidate: the combined RSI/RVOL
/// composite score and the resulting bucket assignment, including a hysteresis exit floor.
/// NoBuyListExpiration is only ever populated when Bucket resolves to Exit - it is the
/// composite-degradation exit's own output, not a passthrough of the caller's
/// noBuyExpiration input.
/// </summary>
public sealed record GateCompositeResult
{
    public required decimal CompositeScore { get; init; }

    public required GateBucket Bucket { get; init; }

    public DateTime? NoBuyListExpiration { get; init; }
}
