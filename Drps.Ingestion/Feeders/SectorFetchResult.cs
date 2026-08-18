using Drps.Shared.Models;

namespace Drps.Ingestion.Feeders;

public class SectorFetchResult
{
    public bool Success { get; set; }

    public IReadOnlyList<RawSectorObservation> Observations { get; set; } = Array.Empty<RawSectorObservation>();

    public string? ErrorMessage { get; set; }

    // Same ambiguity as ExDividendFetchResult.NoDataForRange: a well-formed response with no
    // usable sector classification. Only meaningful when Success is true. Finnhub returns an
    // empty JSON object (200 OK) for both an unrecognized ticker and a real company it simply
    // has no industry classification for - a feeder has no way to tell those apart from the
    // response alone. Orchestration must treat this as neither a real success nor a real
    // failure.
    public bool NoDataForRange { get; set; }
}
