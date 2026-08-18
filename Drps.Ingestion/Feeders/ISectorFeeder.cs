using Drps.Shared.Models;

namespace Drps.Ingestion.Feeders;

// Deliberately separate from IExDividendFeeder/IMarketDataFeeder rather than shoehorned
// into either - sector observations are keyed by SectorSourceType (Finnhub/SecEdgar), not
// SourceType, since the two sources use incompatible taxonomies that are never reconciled
// against each other (see RawSectorObservation's doc comment). No start/end range parameter
// either - a sector/industry classification is a current-state lookup, not a time-series
// fetch.
public interface ISectorFeeder
{
    SectorSourceType Source { get; }

    Task<SectorFetchResult> FetchSectorAsync(string ticker, CancellationToken cancellationToken);
}
