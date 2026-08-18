using Drps.Shared.Models;

namespace Drps.Ingestion.Feeders;

public interface IMarketDataFeeder
{
    SourceType Source { get; }

    Task<FeedFetchResult> FetchDailyBarsAsync(string symbol, DateOnly start, DateOnly end, CancellationToken cancellationToken);
}
