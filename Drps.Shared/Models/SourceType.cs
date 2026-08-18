namespace Drps.Shared.Models;

// Explicit values pinned so Source's persisted int in RawOhlcvBars/SourceStatuses/
// Discrepancies never shifts as members are added or removed - do not renumber existing
// members, ever.
public enum SourceType
{
    Alpaca = 0,

    // 1 = TwelveData, retired 2026-07-12 (permanently disqualified for raw OHLCV bars -
    // Twelve Data retroactively split-adjusts historical daily closes with no
    // "unadjusted" option, see CLAUDE.md). Do not reuse this value.

    AlphaVantage = 2,
    Finnhub = 3,
    Tiingo = 4,

    // Insider Form 4 Adjuster Multiplier Decision (2026-07-19) - SEC EDGAR Form 4 filings,
    // individual purchase-transaction level (not the sector-domain SecEdgarSectorFeeder,
    // which uses the separate SectorSourceType enum for an unrelated data shape).
    SecEdgarForm4 = 5
}
