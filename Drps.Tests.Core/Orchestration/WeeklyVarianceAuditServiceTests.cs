using Drps.Ingestion.Orchestration;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Orchestration;

public class WeeklyVarianceAuditServiceTests
{
    // 2026-07-17 is a Friday - the week-ending anchor. The 7-day window runs 2026-07-11
    // through 2026-07-17 inclusive.
    private static readonly DateOnly WeekEndingDate = new(2026, 7, 17);

    private static RawOhlcvBar MakeBarWithOhlc(
        SourceType source, string symbol, DateOnly date, decimal open, decimal high, decimal low, decimal close) => new()
    {
        Source = source,
        Symbol = symbol,
        Timestamp = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        Resolution = "1Day",
        Open = open,
        High = high,
        Low = low,
        Close = close,
        Volume = 1_000_000,
        AdjustmentType = "raw",
        IngestedAt = DateTimeOffset.UtcNow,
        RequestId = Guid.NewGuid()
    };

    [Fact]
    public async Task RunAsync_BothSourcesPresent_LogsAllFourFieldsWithCorrectVariance()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawOhlcvBars.AddRange(
            MakeBarWithOhlc(SourceType.Alpaca, "AAPL", WeekEndingDate, open: 100.00m, high: 105.00m, low: 99.00m, close: 102.00m),
            MakeBarWithOhlc(SourceType.Tiingo, "AAPL", WeekEndingDate, open: 100.10m, high: 104.90m, low: 99.20m, close: 101.50m));
        await dbContext.SaveChangesAsync();

        var service = new WeeklyVarianceAuditService(dbContext, NullLogger<WeeklyVarianceAuditService>.Instance);
        await service.RunAsync(WeekEndingDate, CancellationToken.None);

        var entries = await dbContext.WeeklyVarianceAuditEntries.Where(e => e.Ticker == "AAPL").ToListAsync();
        Assert.Equal(4, entries.Count);

        var byField = entries.ToDictionary(e => e.Field);
        Assert.Equal(0.10m, byField[OhlcvField.Open].AbsoluteVariance);
        Assert.Equal(0.10m, byField[OhlcvField.High].AbsoluteVariance);
        Assert.Equal(0.20m, byField[OhlcvField.Low].AbsoluteVariance);
        Assert.Equal(0.50m, byField[OhlcvField.Close].AbsoluteVariance);

        var close = byField[OhlcvField.Close];
        Assert.Equal(102.00m, close.AlpacaValue);
        Assert.Equal(101.50m, close.TiingoValue);
        Assert.Equal(0.50m / 102.00m, close.PercentVariance);
        Assert.Equal(WeekEndingDate, close.BarDate); // single-day window in this test
        Assert.Equal(WeekEndingDate, close.WeekEndingDate);

        foreach (var entry in entries)
        {
            Assert.Equal("AAPL", entry.Ticker);
        }
    }

    [Fact]
    public async Task RunAsync_OnlyAlpacaPresent_LogsNothingForThatTickerDate()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawOhlcvBars.Add(
            MakeBarWithOhlc(SourceType.Alpaca, "SOLO", WeekEndingDate, 10m, 11m, 9m, 10.5m));
        await dbContext.SaveChangesAsync();

        var service = new WeeklyVarianceAuditService(dbContext, NullLogger<WeeklyVarianceAuditService>.Instance);
        await service.RunAsync(WeekEndingDate, CancellationToken.None);

        Assert.Empty(dbContext.WeeklyVarianceAuditEntries);
    }

    [Fact]
    public async Task RunAsync_OnlyTiingoPresent_LogsNothingForThatTickerDate()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawOhlcvBars.Add(
            MakeBarWithOhlc(SourceType.Tiingo, "SOLO", WeekEndingDate, 10m, 11m, 9m, 10.5m));
        await dbContext.SaveChangesAsync();

        var service = new WeeklyVarianceAuditService(dbContext, NullLogger<WeeklyVarianceAuditService>.Instance);
        await service.RunAsync(WeekEndingDate, CancellationToken.None);

        Assert.Empty(dbContext.WeeklyVarianceAuditEntries);
    }

    [Fact]
    public async Task RunAsync_DateOutsideSevenDayWindow_IsExcluded()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tooOld = WeekEndingDate.AddDays(-7); // one day before the window starts
        dbContext.RawOhlcvBars.AddRange(
            MakeBarWithOhlc(SourceType.Alpaca, "OLD", tooOld, 10m, 11m, 9m, 10.5m),
            MakeBarWithOhlc(SourceType.Tiingo, "OLD", tooOld, 10.1m, 11.1m, 9.1m, 10.6m));
        await dbContext.SaveChangesAsync();

        var service = new WeeklyVarianceAuditService(dbContext, NullLogger<WeeklyVarianceAuditService>.Instance);
        await service.RunAsync(WeekEndingDate, CancellationToken.None);

        Assert.Empty(dbContext.WeeklyVarianceAuditEntries);
    }

    [Fact]
    public async Task RunAsync_DateAtStartOfWindow_IsIncluded()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var windowStart = WeekEndingDate.AddDays(-6); // inclusive boundary
        dbContext.RawOhlcvBars.AddRange(
            MakeBarWithOhlc(SourceType.Alpaca, "EDGE", windowStart, 10m, 11m, 9m, 10.5m),
            MakeBarWithOhlc(SourceType.Tiingo, "EDGE", windowStart, 10.1m, 11.1m, 9.1m, 10.6m));
        await dbContext.SaveChangesAsync();

        var service = new WeeklyVarianceAuditService(dbContext, NullLogger<WeeklyVarianceAuditService>.Instance);
        await service.RunAsync(WeekEndingDate, CancellationToken.None);

        var entries = await dbContext.WeeklyVarianceAuditEntries.Where(e => e.Ticker == "EDGE").ToListAsync();
        Assert.Equal(4, entries.Count);
        Assert.All(entries, e => Assert.Equal(windowStart, e.BarDate));
    }

    [Fact]
    public async Task RunAsync_InvalidAlpacaValueOnOneField_SkipsOnlyThatFieldNotTheOthers()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Alpaca's Low is a corrupt zero - same "never zero/negative" invalid-data category as
        // BarReconciliationService's Close<=0 guard - but Open/High/Close are all legitimate
        // and must still be logged.
        dbContext.RawOhlcvBars.AddRange(
            MakeBarWithOhlc(SourceType.Alpaca, "BADLOW", WeekEndingDate, open: 10m, high: 11m, low: 0m, close: 10.5m),
            MakeBarWithOhlc(SourceType.Tiingo, "BADLOW", WeekEndingDate, open: 10.1m, high: 11.1m, low: 9.1m, close: 10.6m));
        await dbContext.SaveChangesAsync();

        var service = new WeeklyVarianceAuditService(dbContext, NullLogger<WeeklyVarianceAuditService>.Instance);
        await service.RunAsync(WeekEndingDate, CancellationToken.None);

        var entries = await dbContext.WeeklyVarianceAuditEntries.Where(e => e.Ticker == "BADLOW").ToListAsync();
        Assert.Equal(3, entries.Count);
        Assert.DoesNotContain(entries, e => e.Field == OhlcvField.Low);
        Assert.Contains(entries, e => e.Field == OhlcvField.Open);
        Assert.Contains(entries, e => e.Field == OhlcvField.High);
        Assert.Contains(entries, e => e.Field == OhlcvField.Close);
    }

    [Fact]
    public async Task RunAsync_MultipleTickersMixedAvailability_OnlyDualSourceTickersProduceEntries()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawOhlcvBars.AddRange(
            MakeBarWithOhlc(SourceType.Alpaca, "BOTH", WeekEndingDate, 10m, 11m, 9m, 10.5m),
            MakeBarWithOhlc(SourceType.Tiingo, "BOTH", WeekEndingDate, 10.1m, 11.1m, 9.1m, 10.6m),
            MakeBarWithOhlc(SourceType.Alpaca, "ALPACAONLY", WeekEndingDate, 20m, 21m, 19m, 20.5m));
        await dbContext.SaveChangesAsync();

        var service = new WeeklyVarianceAuditService(dbContext, NullLogger<WeeklyVarianceAuditService>.Instance);
        await service.RunAsync(WeekEndingDate, CancellationToken.None);

        var tickers = (await dbContext.WeeklyVarianceAuditEntries.Select(e => e.Ticker).Distinct().ToListAsync());
        Assert.Equal(new[] { "BOTH" }, tickers);
    }

    [Fact]
    public async Task RunAsync_AppendOnlyDedup_OnlyMostRecentlyIngestedRowPerSourceIsCompared()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var staleAlpaca = MakeBarWithOhlc(SourceType.Alpaca, "REINGEST", WeekEndingDate, 1m, 1m, 1m, 1m);
        staleAlpaca.IngestedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var freshAlpaca = MakeBarWithOhlc(SourceType.Alpaca, "REINGEST", WeekEndingDate, 100m, 100m, 100m, 100m);
        freshAlpaca.IngestedAt = DateTimeOffset.UtcNow;
        var tiingo = MakeBarWithOhlc(SourceType.Tiingo, "REINGEST", WeekEndingDate, 100.5m, 100.5m, 100.5m, 100.5m);

        dbContext.RawOhlcvBars.AddRange(staleAlpaca, freshAlpaca, tiingo);
        await dbContext.SaveChangesAsync();

        var service = new WeeklyVarianceAuditService(dbContext, NullLogger<WeeklyVarianceAuditService>.Instance);
        await service.RunAsync(WeekEndingDate, CancellationToken.None);

        var closeEntry = await dbContext.WeeklyVarianceAuditEntries.SingleAsync(e => e.Ticker == "REINGEST" && e.Field == OhlcvField.Close);
        Assert.Equal(100m, closeEntry.AlpacaValue); // the fresh row, not the stale 1m one
    }

    [Fact]
    public async Task RunAsync_CalledTwiceForTheSameWeek_AppendsDuplicateRowsRatherThanDeduplicating()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawOhlcvBars.AddRange(
            MakeBarWithOhlc(SourceType.Alpaca, "AAPL", WeekEndingDate, 10m, 11m, 9m, 10.5m),
            MakeBarWithOhlc(SourceType.Tiingo, "AAPL", WeekEndingDate, 10.1m, 11.1m, 9.1m, 10.6m));
        await dbContext.SaveChangesAsync();

        var service = new WeeklyVarianceAuditService(dbContext, NullLogger<WeeklyVarianceAuditService>.Instance);
        await service.RunAsync(WeekEndingDate, CancellationToken.None);
        var countAfterFirstRun = await dbContext.WeeklyVarianceAuditEntries.CountAsync();

        await service.RunAsync(WeekEndingDate, CancellationToken.None);
        var countAfterSecondRun = await dbContext.WeeklyVarianceAuditEntries.CountAsync();

        // Explicitly append-only, per CLAUDE.md - a re-run for the same week is not deduplicated,
        // same "logged raw, no deduplication" precedent as Discrepancy.
        Assert.Equal(countAfterFirstRun * 2, countAfterSecondRun);
    }

    [Fact]
    public async Task RunAsync_NoBarsAtAll_LogsNothingAndDoesNotThrow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var service = new WeeklyVarianceAuditService(dbContext, NullLogger<WeeklyVarianceAuditService>.Instance);

        await service.RunAsync(WeekEndingDate, CancellationToken.None);

        Assert.Empty(dbContext.WeeklyVarianceAuditEntries);
    }
}
