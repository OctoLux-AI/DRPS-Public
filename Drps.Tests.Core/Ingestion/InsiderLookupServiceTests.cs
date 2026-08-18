using Drps.Ingestion.Persistence;
using Drps.Ingestion.Verification;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;

namespace Drps.Tests.Ingestion;

public class InsiderLookupServiceTests
{
    private static readonly DateTime AsOf = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly AsOfDate = DateOnly.FromDateTime(AsOf);

    private static RawOhlcvBar MakeBar(string symbol, DateOnly date, long volume, decimal close) => new()
    {
        Source = SourceType.Alpaca,
        Symbol = symbol,
        Timestamp = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        Resolution = "1Day",
        Open = close,
        High = close,
        Low = close,
        Close = close,
        Volume = volume,
        AdjustmentType = "raw",
        IngestedAt = DateTimeOffset.UtcNow,
        RequestId = Guid.NewGuid()
    };

    // 20 identical daily bars (Volume=10,000, Close=100 -> $1,000,000 dollar volume/day),
    // ending at AsOfDate - a clean, known average-daily-dollar-volume denominator every
    // ratio-dependent test below builds on.
    private static void SeedTwentyDaysOfBars(DrpsDbContext dbContext, string ticker)
    {
        for (var i = 0; i < 20; i++)
        {
            dbContext.RawOhlcvBars.Add(MakeBar(ticker, AsOfDate.AddDays(-i), volume: 10_000, close: 100m));
        }
    }

    private static RawInsiderObservation MakePurchase(string ticker, DateOnly transactionDate, decimal dollarValue) => new()
    {
        Ticker = ticker,
        Source = SourceType.SecEdgarForm4,
        TransactionDate = transactionDate,
        DollarValue = dollarValue,
        InsiderName = "TEST INSIDER",
        FetchedAt = DateTime.UtcNow,
        Verified = true
    };

    // Matches EdgarForm4Feeder.BuildScannedCleanObservation's exact shape - the real row a
    // completed, zero-purchase scan leaves behind. Verified=true, DollarValue=0m,
    // InsiderName=null - distinct from a failure row (Verified=false) and from "no row at
    // all" (never scanned).
    private static RawInsiderObservation MakeScannedCleanMarker(string ticker, DateOnly transactionDate) => new()
    {
        Ticker = ticker,
        Source = SourceType.SecEdgarForm4,
        TransactionDate = transactionDate,
        DollarValue = 0m,
        InsiderName = null,
        FetchedAt = DateTime.UtcNow,
        Verified = true
    };

    [Fact]
    public async Task GetMultiplierAsync_NoVolumeHistory_ReturnsNeutralAndFlagsUnverified()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Deliberately no RawOhlcvBars seeded at all.

        var service = new InsiderLookupService(dbContext);
        var result = await service.GetMultiplierAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(1.0m, result.Multiplier);
        Assert.True(result.IsDataUnverified);
    }

    [Fact]
    public async Task GetMultiplierAsync_InsufficientVolumeHistory_ReturnsNeutralAndFlagsUnverified()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Only 5 bars - fewer than the 20-day window this lookup requires.
        for (var i = 0; i < 5; i++)
        {
            dbContext.RawOhlcvBars.Add(MakeBar("AAPL", AsOfDate.AddDays(-i), volume: 10_000, close: 100m));
        }
        await dbContext.SaveChangesAsync();

        var service = new InsiderLookupService(dbContext);
        var result = await service.GetMultiplierAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(1.0m, result.Multiplier);
        Assert.True(result.IsDataUnverified);
    }

    [Fact]
    public async Task GetMultiplierAsync_NeverScannedTicker_ReturnsNeutralAndFlagsUnverified()
    {
        // Volume history exists (a real, computable ratio denominator), but zero
        // RawInsiderObservation rows of any kind exist for this ticker - EdgarForm4Feeder has
        // simply never run against it. A real, unambiguous data gap, not a confirmed-clean
        // answer - must not be conflated with the scanned-clean case below.
        using var dbContext = InMemoryDbContextFactory.Create();
        SeedTwentyDaysOfBars(dbContext, "AAPL");
        await dbContext.SaveChangesAsync();

        var service = new InsiderLookupService(dbContext);
        var result = await service.GetMultiplierAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(1.0m, result.Multiplier);
        Assert.True(result.IsDataUnverified);
    }

    [Fact]
    public async Task GetMultiplierAsync_ScannedCleanMarkerRowOnly_ReturnsNeutralWithoutUnverifiedFlag()
    {
        // Same volume history as the never-scanned test above, but this ticker HAS been
        // scanned - EdgarForm4Feeder's own Verified=true, zero-value marker row is present,
        // confirming a completed scan that genuinely found nothing. Same numeric Multiplier
        // as the never-scanned case, but IsDataUnverified must now read false.
        using var dbContext = InMemoryDbContextFactory.Create();
        SeedTwentyDaysOfBars(dbContext, "AAPL");
        dbContext.RawInsiderObservations.Add(MakeScannedCleanMarker("AAPL", AsOfDate));
        await dbContext.SaveChangesAsync();

        var service = new InsiderLookupService(dbContext);
        var result = await service.GetMultiplierAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(1.0m, result.Multiplier);
        Assert.False(result.IsDataUnverified);
    }

    [Fact]
    public async Task GetMultiplierAsync_NeverScannedVersusFailedScanVersusScannedClean_AreAllDistinguishableInOutput()
    {
        // Direct three-way side-by-side contrast: identical volume history for all three
        // tickers, the only difference is what (if anything) EdgarForm4Feeder recorded for
        // each - proving all three states are genuinely distinguishable through this
        // service's output, not just in theory. Every one resolves to the same neutral
        // Multiplier; only IsDataUnverified tells them apart.
        using var dbContext = InMemoryDbContextFactory.Create();
        SeedTwentyDaysOfBars(dbContext, "NEVR");
        SeedTwentyDaysOfBars(dbContext, "FAIL");
        SeedTwentyDaysOfBars(dbContext, "CLEN");
        // NEVR: zero RawInsiderObservation rows at all - never scanned.
        dbContext.RawInsiderObservations.Add(new RawInsiderObservation
        {
            Ticker = "FAIL",
            Source = SourceType.SecEdgarForm4,
            TransactionDate = AsOfDate,
            DollarValue = 0m,
            FetchedAt = DateTime.UtcNow,
            Verified = false
        });
        dbContext.RawInsiderObservations.Add(MakeScannedCleanMarker("CLEN", AsOfDate));
        await dbContext.SaveChangesAsync();

        var service = new InsiderLookupService(dbContext);
        var neverScanned = await service.GetMultiplierAsync("NEVR", AsOf, CancellationToken.None);
        var failedScan = await service.GetMultiplierAsync("FAIL", AsOf, CancellationToken.None);
        var scannedClean = await service.GetMultiplierAsync("CLEN", AsOf, CancellationToken.None);

        Assert.Equal(1.0m, neverScanned.Multiplier);
        Assert.Equal(1.0m, failedScan.Multiplier);
        Assert.Equal(1.0m, scannedClean.Multiplier);

        Assert.True(neverScanned.IsDataUnverified);
        Assert.True(failedScan.IsDataUnverified);
        Assert.False(scannedClean.IsDataUnverified);
    }

    [Fact]
    public async Task GetMultiplierAsync_PurchaseOutsideSixtyDayWindow_IsExcludedFromTheSum()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        SeedTwentyDaysOfBars(dbContext, "AAPL");
        // 61 days back - just outside the trailing 60-day window.
        dbContext.RawInsiderObservations.Add(MakePurchase("AAPL", AsOfDate.AddDays(-61), 500_000m));
        await dbContext.SaveChangesAsync();

        var service = new InsiderLookupService(dbContext);
        var result = await service.GetMultiplierAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(1.0m, result.Multiplier);
        Assert.False(result.IsDataUnverified);
    }

    [Fact]
    public async Task GetMultiplierAsync_OnlyFailedScanRecorded_ReturnsNeutralAndFlagsUnverified()
    {
        // Only a Verified=false failure row exists - EdgarForm4Feeder's own fail-closed
        // sentinel from a scan attempt that never actually succeeded. A failed attempt is
        // not evidence anything was successfully checked, so this must read identically to
        // the never-scanned case above (IsDataUnverified=true), NOT like the scanned-clean
        // case - distinct from both.
        using var dbContext = InMemoryDbContextFactory.Create();
        SeedTwentyDaysOfBars(dbContext, "AAPL");
        dbContext.RawInsiderObservations.Add(new RawInsiderObservation
        {
            Ticker = "AAPL",
            Source = SourceType.SecEdgarForm4,
            TransactionDate = AsOfDate.AddDays(-10),
            DollarValue = 0m,
            FetchedAt = DateTime.UtcNow,
            Verified = false
        });
        await dbContext.SaveChangesAsync();

        var service = new InsiderLookupService(dbContext);
        var result = await service.GetMultiplierAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(1.0m, result.Multiplier);
        Assert.True(result.IsDataUnverified);
    }

    [Fact]
    public async Task GetMultiplierAsync_DifferentTicker_DoesNotSeeAnotherTickersPurchases()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        SeedTwentyDaysOfBars(dbContext, "AAPL");
        SeedTwentyDaysOfBars(dbContext, "MSFT");
        // AAPL has its own scanned-clean marker (a real, completed scan) - MSFT's large
        // purchase must not leak into AAPL's result regardless.
        dbContext.RawInsiderObservations.Add(MakeScannedCleanMarker("AAPL", AsOfDate));
        dbContext.RawInsiderObservations.Add(MakePurchase("MSFT", AsOfDate.AddDays(-10), 10_000_000m));
        await dbContext.SaveChangesAsync();

        var service = new InsiderLookupService(dbContext);
        var result = await service.GetMultiplierAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(1.0m, result.Multiplier);
        Assert.False(result.IsDataUnverified);
    }

    // --- Pure ComputeMultiplier curve tests ---

}
