using Drps.Shared.Models;

namespace Drps.Ingestion.Feeders;

public class FeedFetchResult
{
    public bool Success { get; set; }

    public IReadOnlyList<RawOhlcvBar> Bars { get; set; } = Array.Empty<RawOhlcvBar>();

    public string? ErrorMessage { get; set; }

    // True when the source returned a well-formed, non-error response containing zero
    // bars for the requested range. Only meaningful when Success is true. Deliberately
    // ambiguous by design, not a "confirmed not found" signal like SymbolNotFoundException
    // — a source can return this same shape for an invalid/delisted ticker or for a
    // legitimate empty range (a market holiday, a gap before a ticker's IPO date), and a
    // feeder has no way to tell those apart from the response alone. Orchestration must
    // treat this as neither a real success (must not count toward source-trust promotion)
    // nor a real failure (must not strike ConsecutiveFailures).
    public bool NoDataForRange { get; set; }

    // Populated only by TiingoFeeder today (empty for every other IMarketDataFeeder,
    // including AlpacaFeeder) — CLAUDE.md's "Ex-Dividend Source: Tiingo Replaces Finnhub"
    // decision (2026-08-01). Tiingo's daily-bars response already carries a divCash field
    // on every entry, so this rides the single existing OHLCV HTTP call rather than a
    // second one; extraction stays in a dedicated mapper (TiingoExDividendMapper), kept
    // deliberately separate from bar mapping so RawOhlcvBar's own mapping path never has to
    // know dividends exist. IngestionRunner writes this list into RawExDividendObservations
    // independently of the RawOhlcvBars write above — a distinct table, distinct entity
    // type, no merging of the two domains' write paths.
    public IReadOnlyList<RawExDividendObservation> ExDividendObservations { get; set; } = Array.Empty<RawExDividendObservation>();
}
