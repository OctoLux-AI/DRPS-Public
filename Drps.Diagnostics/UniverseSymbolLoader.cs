namespace Drps.Diagnostics;

// Reuses the free, unauthenticated NASDAQ Trader symbol-directory files already named as
// DRPS's intended universe source (CLAUDE.md, "Universe Source and Persistence") - the same
// files CapitalFill already pulls from. This diagnostic only needs a large pool of REAL,
// currently-listed ticker symbols to batch-test against Alpaca with; it does not persist,
// dedupe-for-eligibility, or otherwise stand in for the real universe-ingestion service that
// section describes as "designed, not yet ported."
public static class UniverseSymbolLoader
{
    private const string NasdaqListedUrl = "https://www.nasdaqtrader.com/dynamic/SymDir/nasdaqlisted.txt";
    private const string OtherListedUrl = "https://www.nasdaqtrader.com/dynamic/SymDir/otherlisted.txt";

    public static async Task<IReadOnlyList<string>> LoadRealSymbolsAsync(HttpClient httpClient, CancellationToken cancellationToken)
    {
        var symbols = new List<string>();
        symbols.AddRange(await LoadFileAsync(httpClient, NasdaqListedUrl, "Symbol", "Test Issue", cancellationToken));
        symbols.AddRange(await LoadFileAsync(httpClient, OtherListedUrl, "ACT Symbol", "Test Issue", cancellationToken));

        // Dedup (a handful of symbols appear in both directory files under cross-listing edge
        // cases), and restrict to plain single-class equity tickers (letters only, <=5 chars) -
        // strips warrant/unit/preferred/rights suffixes (e.g. "ABC.W", "ABC$A") that Alpaca's
        // plain-symbol endpoint isn't expected to resolve cleanly. Those would just muddy the
        // "did this batch call actually get truncated" signal this probe exists to read, since
        // a real "no bars for this symbol" and a "we don't carry this instrument" both look
        // identical on the wire (same NoDataForRange-shaped ambiguity documented elsewhere in
        // CLAUDE.md for AlpacaFeeder itself).
        return symbols
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(s => s.Length is >= 1 and <= 5 && s.All(char.IsAsciiLetterUpper))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<List<string>> LoadFileAsync(
        HttpClient httpClient, string url, string symbolColumn, string testIssueColumn, CancellationToken cancellationToken)
    {
        var text = await httpClient.GetStringAsync(url, cancellationToken);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 2)
            return [];

        var headers = lines[0].Split('|');
        var symbolIndex = Array.IndexOf(headers, symbolColumn);
        var testIssueIndex = Array.IndexOf(headers, testIssueColumn);
        if (symbolIndex < 0)
            return [];

        var result = new List<string>();

        // Skipped implicitly rather than special-cased: the file's final line is a
        // "File Creation Time: ..." footer, not a data row - it never has enough pipe-delimited
        // fields to reach symbolIndex, so the column-count guard below drops it on its own.
        for (var i = 1; i < lines.Length; i++)
        {
            var fields = lines[i].Split('|');
            if (fields.Length <= symbolIndex)
                continue;
            if (testIssueIndex >= 0 && fields.Length > testIssueIndex && fields[testIssueIndex] == "Y")
                continue;

            var symbol = fields[symbolIndex].Trim();
            if (!string.IsNullOrEmpty(symbol))
                result.Add(symbol);
        }

        return result;
    }
}
