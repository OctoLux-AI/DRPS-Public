using System.Text.Json;
using Drps.Shared.Models;

namespace Drps.Ingestion.Finnhub;

public static class FinnhubBarMapper
{
    public static IReadOnlyList<OhlcvBar> MapBars(string ticker, string responseJson, DateTime fetchedAt)
    {
        var bars = new List<OhlcvBar>();

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        // "s" is the status field: "ok" means data follows in the parallel c/h/l/o/t/v
        // arrays; "no_data" means the range had no trading data (empty result, not an
        // error). Any other status is treated the same as "no_data" here — this method
        // only maps, it doesn't retry or raise.
        var status = root.TryGetProperty("s", out var statusElement) ? statusElement.GetString() : null;
        if (status != "ok")
            return bars;

        var opens = root.GetProperty("o");
        var highs = root.GetProperty("h");
        var lows = root.GetProperty("l");
        var closes = root.GetProperty("c");
        var volumes = root.GetProperty("v");
        var timestamps = root.GetProperty("t");

        var count = timestamps.GetArrayLength();
        for (var i = 0; i < count; i++)
        {
            bars.Add(new OhlcvBar(
                Ticker: ticker,
                BarDate: DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime),
                Open: opens[i].GetDecimal(),
                High: highs[i].GetDecimal(),
                Low: lows[i].GetDecimal(),
                Close: closes[i].GetDecimal(),
                Volume: volumes[i].GetInt64(),
                Source: "Finnhub",
                FetchedAt: fetchedAt,
                SampleCount: 1,
                VariancePct: null,
                Verified: false
            ));
        }

        return bars;
    }
}
