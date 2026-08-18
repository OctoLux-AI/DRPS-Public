using Drps.Shared.Models;

namespace Drps.Ingestion.Feeders;

public class RegimeFetchResult
{
    public bool Success { get; set; }

    public IReadOnlyList<RawRegimeObservation> Observations { get; set; } = Array.Empty<RawRegimeObservation>();

    public string? ErrorMessage { get; set; }
}
