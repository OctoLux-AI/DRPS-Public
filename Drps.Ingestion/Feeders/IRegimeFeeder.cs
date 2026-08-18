using Drps.Shared.Models;

namespace Drps.Ingestion.Feeders;

// Deliberately not shaped like ISectorFeeder (no ticker method parameter) - each concrete
// implementation is permanently bound to exactly one fixed index (VIX/VXN/VIX3M) and one
// fixed source (Cboe direct/FRED), unlike the sector feeders' generic-across-any-stock-
// ticker shape. Also unlike ISectorFeeder/IMarketDataFeeder, there is no date-range
// parameter: Cboe's and FRED's CSV exports return their entire history on every call, with
// no query-string date-range parameter to narrow the request.
public interface IRegimeFeeder
{
    string Ticker { get; }

    RegimeSourceType Source { get; }

    Task<RegimeFetchResult> FetchHistoryAsync(CancellationToken cancellationToken);
}
