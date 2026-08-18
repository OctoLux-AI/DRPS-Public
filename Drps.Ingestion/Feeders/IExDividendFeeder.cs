using Drps.Shared.Models;

namespace Drps.Ingestion.Feeders;

// Deliberately separate from IMarketDataFeeder rather than shoehorned into it -
// ex-dividend observations are a different shape (RawExDividendObservation, not
// RawOhlcvBar) with no analogous OHLCV concept, so forcing one interface to cover both
// would corrupt IMarketDataFeeder's existing bar-shaped contract for no benefit.
public interface IExDividendFeeder
{
    SourceType Source { get; }

    Task<ExDividendFetchResult> FetchExDividendsAsync(string symbol, DateOnly start, DateOnly end, CancellationToken cancellationToken);
}
