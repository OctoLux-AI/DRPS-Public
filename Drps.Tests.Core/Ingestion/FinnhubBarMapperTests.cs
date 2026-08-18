using Drps.Ingestion.Finnhub;

namespace Drps.Tests.Ingestion;

public class FinnhubBarMapperTests
{
    [Fact]
    public void MapBars_ParsesParallelArraysAndAppliesUnverifiedSingleSourceProvenance()
    {
        var day1 = new DateTimeOffset(2026, 7, 6, 0, 0, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);
        var fetchedAt = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

        var json = $$"""
        {
            "c": [211.90, 212.40],
            "h": [212.30, 213.00],
            "l": [209.80, 210.50],
            "o": [210.50, 211.90],
            "s": "ok",
            "t": [{{day1.ToUnixTimeSeconds()}}, {{day2.ToUnixTimeSeconds()}}],
            "v": [45123456, 40234567]
        }
        """;

        var bars = FinnhubBarMapper.MapBars("AAPL", json, fetchedAt);

        Assert.Equal(2, bars.Count);

        var first = bars[0];
        Assert.Equal("AAPL", first.Ticker);
        Assert.Equal(new DateOnly(2026, 7, 6), first.BarDate);
        Assert.Equal(210.50m, first.Open);
        Assert.Equal(212.30m, first.High);
        Assert.Equal(209.80m, first.Low);
        Assert.Equal(211.90m, first.Close);
        Assert.Equal(45123456L, first.Volume);
        Assert.Equal("Finnhub", first.Source);
        Assert.Equal(fetchedAt, first.FetchedAt);
        Assert.Equal(1, first.SampleCount);
        Assert.Null(first.VariancePct);
        Assert.False(first.Verified);

        var second = bars[1];
        Assert.Equal(new DateOnly(2026, 7, 7), second.BarDate);
        Assert.Equal(212.40m, second.Close);
        Assert.False(second.Verified);
    }

    [Fact]
    public void MapBars_NoDataStatus_ReturnsEmptyList()
    {
        var bars = FinnhubBarMapper.MapBars("ZZZZ", """{"s": "no_data"}""", DateTime.UtcNow);

        Assert.Empty(bars);
    }
}
