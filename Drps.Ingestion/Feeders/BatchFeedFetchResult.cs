using Drps.Shared.Models;

namespace Drps.Ingestion.Feeders;

// Batched counterpart to FeedFetchResult - keyed by symbol rather than a flat bar list, since
// a single batch call covers many symbols at once and the caller (UniverseBarSweepRunner)
// needs to know which of the requested symbols actually came back with data. A symbol absent
// from BarsBySymbol means "no bars for this symbol in this pull" - the same ambiguous shape
// (delisted/halted/genuinely no data) FeedFetchResult.NoDataForRange already documents for
// the single-symbol case, just represented as absence instead of a bool flag here.
public class BatchFeedFetchResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyList<RawOhlcvBar>> BarsBySymbol { get; init; } =
        new Dictionary<string, IReadOnlyList<RawOhlcvBar>>();

    // Alpaca's ~10,000-data-point response cap can still be crossed within a single batch if
    // the caller's date range is wide enough, even at the confirmed 500-symbol/1-day-pull
    // shape the nightly sweep actually uses. Surfaced so the caller can log it rather than
    // silently miss the paginated remainder - see AlpacaBatchBarFeeder's own doc comment for
    // why pagination itself is not implemented in v1.
    public bool NextPageTokenPresent { get; init; }
}
