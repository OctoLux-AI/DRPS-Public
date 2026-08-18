namespace Drps.Ingestion.Feeders;

// Interface seam purely for testability (UniverseBarSweepRunnerTests fakes this directly,
// same precedent as IMarketDataFeeder/IExDividendFeeder/ISectorFeeder), not for multiple
// production implementations - AlpacaBatchBarFeeder is the only one, and it is
// deliberately Alpaca-only (Two-Tier Verification Cost Model: the full nightly sweep is
// single-source by design, dual-source verification is a separate, narrower, already-existing
// path).
public interface IAlpacaBatchBarFeeder
{
    Task<BatchFeedFetchResult> FetchBatchAsync(
        IReadOnlyList<string> symbols, DateOnly start, DateOnly end, CancellationToken cancellationToken);
}
