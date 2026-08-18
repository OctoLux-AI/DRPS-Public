using Drps.Shared.Models;

namespace Drps.Ingestion.Feeders;

public class ExDividendFetchResult
{
    public bool Success { get; set; }

    public IReadOnlyList<RawExDividendObservation> Observations { get; set; } = Array.Empty<RawExDividendObservation>();

    public string? ErrorMessage { get; set; }

    // Same ambiguity as FeedFetchResult.NoDataForRange: a well-formed response with zero
    // ex-dividend events for the requested range. Only meaningful when Success is true. A
    // source can return this same shape for an invalid ticker or for a legitimate
    // non-dividend-paying stock, and a feeder has no way to tell those apart from the
    // response alone. Orchestration must treat this as neither a real success (must not
    // count toward source-trust promotion) nor a real failure (must not strike
    // ConsecutiveFailures).
    public bool NoDataForRange { get; set; }
}
