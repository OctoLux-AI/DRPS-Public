namespace Drps.Shared.Helpers;

// Ported verbatim from CapitalFill's Solon.Shared.Helpers.TickerSymbolHelper (Solonosphere
// repo) - same rules, unchanged, per this task's explicit "reuse as-is" scope. Filters out
// warrants/rights/preferred-share suffixes (W/WS/WR/WT/R/RT/P/PR/U) and pure-digit strings
// that aren't real single-class equity tickers, so the NASDAQ Trader symbol-directory files'
// raw rows don't pollute the universe with instruments this codebase has no use for.
public static class TickerSymbolHelper
{
    public static bool IsValidSymbol(string ticker)
    {
        if (string.IsNullOrWhiteSpace(ticker)) return false;
        if (ticker.Length > 5) return false;
        if (!ticker.All(char.IsLetter)) return false;
        if (ticker.EndsWith("WS") || ticker.EndsWith("WR") || ticker.EndsWith("WT") ||
            ticker.EndsWith("RT") || ticker.EndsWith("PR")) return false;
        if (ticker.EndsWith('W') || ticker.EndsWith('R') || ticker.EndsWith('P') || ticker.EndsWith('U')) return false;
        if (ticker.All(char.IsDigit)) return false;
        return true;
    }
}
