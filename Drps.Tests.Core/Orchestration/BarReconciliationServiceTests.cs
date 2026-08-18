using Drps.Ingestion.Orchestration;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Orchestration;

public class BarReconciliationServiceTests
{
    private static readonly DateOnly Day = new(2026, 7, 9);
    private static readonly DateTimeOffset DayTimestamp = new(2026, 7, 9, 0, 0, 0, TimeSpan.Zero);

    private static RawOhlcvBar MakeBar(SourceType source, string symbol, decimal close) => new()
    {
        Source = source,
        Symbol = symbol,
        Timestamp = DayTimestamp,
        Resolution = "1Day",
        Open = close - 1m,
        High = close + 1m,
        Low = close - 2m,
        Close = close,
        Volume = 1_000_000,
        AdjustmentType = "raw",
        IngestedAt = DateTimeOffset.UtcNow,
        RequestId = Guid.NewGuid()
    };

    // Explicit OHLC control, unlike MakeBar's fixed offsets-from-Close - needed for the
    // OHL-agreed/Close-disagreed exception tests, where Open/High/Low must independently
    // agree between sources while Close does not.
    private static RawOhlcvBar MakeBarWithOhlc(
        SourceType source, string symbol, decimal open, decimal high, decimal low, decimal close, long volume = 1_000_000) => new()
    {
        Source = source,
        Symbol = symbol,
        Timestamp = DayTimestamp,
        Resolution = "1Day",
        Open = open,
        High = high,
        Low = low,
        Close = close,
        Volume = volume,
        AdjustmentType = "raw",
        IngestedAt = DateTimeOffset.UtcNow,
        RequestId = Guid.NewGuid()
    };

    [Fact]
    public async Task ReconcileAsync_AlpacaAndTiingoWithinTolerance_VerifiedTrueMatchedTwoPrimaryIsAlpacaClose()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawOhlcvBars.AddRange(
            MakeBar(SourceType.Alpaca, "AAPL", 211.90m),
            MakeBar(SourceType.Tiingo, "AAPL", 212.00m)); // ~0.047% variance
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);
        await service.ReconcileAsync("AAPL", Day, Day, 1, CancellationToken.None);

        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.True(verification.Verified);
        Assert.Equal(2, verification.MatchedSourceCount);
        Assert.Equal(211.90m, verification.PrimarySourceValue);
    }

    [Fact]
    public async Task ReconcileAsync_AlpacaAndTiingoBeyondTolerance_VerifiedFalseMatchedOneAndDiscrepancyLogged()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawOhlcvBars.AddRange(
            MakeBar(SourceType.Alpaca, "AAPL", 211.90m),
            MakeBar(SourceType.Tiingo, "AAPL", 220.00m)); // ~3.82% variance
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);
        await service.ReconcileAsync("AAPL", Day, Day, 1, CancellationToken.None);

        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.False(verification.Verified);
        Assert.Equal(1, verification.MatchedSourceCount);
        Assert.Equal(211.90m, verification.PrimarySourceValue);

        var discrepancy = await dbContext.Discrepancies.SingleAsync();
        // Row order (which source lands in SourceA vs SourceB) isn't part of the contract —
        // assert on the pairing of source-to-value regardless of orientation.
        Assert.Equal(
            new HashSet<SourceType> { SourceType.Alpaca, SourceType.Tiingo },
            new HashSet<SourceType> { discrepancy.SourceA, discrepancy.SourceB });
        var alpacaValue = discrepancy.SourceA == SourceType.Alpaca ? discrepancy.ValueA : discrepancy.ValueB;
        var tiingoValue = discrepancy.SourceA == SourceType.Tiingo ? discrepancy.ValueA : discrepancy.ValueB;
        Assert.Equal(211.90m, alpacaValue);
        Assert.Equal(220.00m, tiingoValue);
        Assert.True(discrepancy.PercentDiff > 0.001m);
    }

    [Fact]
    public async Task ReconcileAsync_OnlyTiingoNoAlpaca_PrimaryNullSourceCountOneMatchedOneUnverified()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawOhlcvBars.Add(MakeBar(SourceType.Tiingo, "AAPL", 212.00m));
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);
        await service.ReconcileAsync("AAPL", Day, Day, 1, CancellationToken.None);

        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.Null(verification.PrimarySourceValue);
        Assert.Equal(1, verification.SourceCount);
        Assert.Equal(1, verification.MatchedSourceCount);
        Assert.False(verification.Verified);
    }

    [Fact]
    public async Task ReconcileAsync_TwoDuplicateBarsFromSameSourceOnly_SourceCountOneMatchedOneStillUnverified()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Simulates a re-run appending a second Alpaca row for the same day (append-only
        // ingestion) with no second source ever reporting. The duplicate-row dedup collapses
        // these to a single comparison candidate, so matchedCount can only ever be 1 for a
        // lone source — sourceCount and matchedCount both correctly gate this as unverified.
        dbContext.RawOhlcvBars.AddRange(
            MakeBar(SourceType.Alpaca, "AAPL", 211.90m),
            MakeBar(SourceType.Alpaca, "AAPL", 211.90m));
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);
        await service.ReconcileAsync("AAPL", Day, Day, 1, CancellationToken.None);

        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.Equal(1, verification.SourceCount);
        Assert.Equal(1, verification.MatchedSourceCount);
        Assert.False(verification.Verified);
    }

    [Fact]
    public async Task ReconcileAsync_SourceHasMultipleHistoricalRowsForSameDay_LogsExactlyOneDiscrepancyPerPair()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var baseTime = DateTimeOffset.UtcNow.AddDays(-1);

        // Simulates append-only re-ingestion: Alpaca has been fetched three times for the
        // same day across separate runs, leaving three historical rows for one source.
        // Only the most recently ingested Alpaca row should be a live comparison candidate.
        var alpacaOldest = MakeBar(SourceType.Alpaca, "AAPL", 211.90m);
        alpacaOldest.IngestedAt = baseTime;
        var alpacaMiddle = MakeBar(SourceType.Alpaca, "AAPL", 211.90m);
        alpacaMiddle.IngestedAt = baseTime.AddHours(1);
        var alpacaLatest = MakeBar(SourceType.Alpaca, "AAPL", 211.90m);
        alpacaLatest.IngestedAt = baseTime.AddHours(2);
        var tiingo = MakeBar(SourceType.Tiingo, "AAPL", 220.00m); // ~3.82% variance

        dbContext.RawOhlcvBars.AddRange(alpacaOldest, alpacaMiddle, alpacaLatest, tiingo);
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);
        await service.ReconcileAsync("AAPL", Day, Day, 1, CancellationToken.None);

        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.Equal(2, verification.SourceCount);

        var discrepancy = await dbContext.Discrepancies.SingleAsync();
        Assert.Equal(
            new HashSet<SourceType> { SourceType.Alpaca, SourceType.Tiingo },
            new HashSet<SourceType> { discrepancy.SourceA, discrepancy.SourceB });
    }

    [Fact]
    public async Task ReconcileAsync_ThreeSourcesNoAlpacaTwoAgreeOneOutlier_MatchedTwoAndDiscrepanciesOnlyForOutlierPairs()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawOhlcvBars.AddRange(
            MakeBar(SourceType.Tiingo, "AAPL", 100.00m),
            MakeBar(SourceType.AlphaVantage, "AAPL", 100.05m), // agrees with Tiingo, ~0.05%
            MakeBar(SourceType.Finnhub, "AAPL", 110.00m));      // outlier, ~10% off both
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);
        await service.ReconcileAsync("AAPL", Day, Day, 1, CancellationToken.None);

        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.Null(verification.PrimarySourceValue);
        Assert.Equal(3, verification.SourceCount);
        Assert.Equal(2, verification.MatchedSourceCount);
        Assert.True(verification.Verified);

        var discrepancies = await dbContext.Discrepancies.ToListAsync();
        Assert.Equal(2, discrepancies.Count);
        Assert.All(discrepancies, d => Assert.True(d.SourceA == SourceType.Finnhub || d.SourceB == SourceType.Finnhub));
    }

    [Fact]
    public async Task ReconcileAsync_CalledTwiceOverSameBars_UpsertsBarVerificationButAccumulatesDiscrepancies()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawOhlcvBars.AddRange(
            MakeBar(SourceType.Alpaca, "AAPL", 211.90m),
            MakeBar(SourceType.Tiingo, "AAPL", 220.00m));
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);
        await service.ReconcileAsync("AAPL", Day, Day, 1, CancellationToken.None);
        await service.ReconcileAsync("AAPL", Day, Day, 1, CancellationToken.None);

        Assert.Equal(1, await dbContext.BarVerifications.CountAsync());

        // Discrepancy rows are append-only per CLAUDE.md — each call re-detects the same
        // disagreement and logs a fresh row. This is expected, not a bug: assert on it
        // explicitly rather than assuming dedup.
        Assert.Equal(2, await dbContext.Discrepancies.CountAsync());
    }

    [Fact]
    public async Task ReconcileAsync_OneZeroCloseBarAndOneValidBar_ExcludesZeroBarAndReconcilesOnlyTheValidOne()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawOhlcvBars.AddRange(
            MakeBar(SourceType.Alpaca, "AAPL", 212.00m),
            MakeBar(SourceType.Tiingo, "AAPL", 0m));
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);

        var exception = await Record.ExceptionAsync(() => service.ReconcileAsync("AAPL", Day, Day, 1, CancellationToken.None));

        Assert.Null(exception);

        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.Equal(1, verification.SourceCount);
        Assert.Equal(1, verification.MatchedSourceCount);
        Assert.False(verification.Verified);
        Assert.Equal(212.00m, verification.PrimarySourceValue);

        Assert.Empty(dbContext.Discrepancies);
    }

    [Fact]
    public async Task ReconcileAsync_LowPricedPairWithinAbsoluteFloorButBeyondPercentTolerance_VerifiedTrueNoDiscrepancy()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Reproduces the PLUG basket-test case (2026-07-12): ~$2.45 price, $0.01 diff —
        // 0.408% variance fails the pure 0.1% tolerance, but $0.01 is well within the
        // $0.02 absolute floor, so the hybrid check should now pass this bar.
        dbContext.RawOhlcvBars.AddRange(
            MakeBar(SourceType.Alpaca, "PLUG", 2.45m),
            MakeBar(SourceType.Tiingo, "PLUG", 2.46m));
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);
        await service.ReconcileAsync("PLUG", Day, Day, 1, CancellationToken.None);

        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.True(verification.Verified);
        Assert.Equal(2, verification.MatchedSourceCount);

        Assert.Empty(dbContext.Discrepancies);
    }

    [Fact]
    public async Task ReconcileAsync_MidPricedPairBeyondBothPercentAndAbsoluteTolerance_VerifiedFalseAndDiscrepancyLogged()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Reproduces the CIFR basket-test case (2026-07-12): ~$20.57 price, $0.10 diff —
        // 0.486% variance fails the 0.1% tolerance, and $0.10 also exceeds the $0.02
        // absolute floor, so this must still fail under the hybrid check.
        dbContext.RawOhlcvBars.AddRange(
            MakeBar(SourceType.Alpaca, "CIFR", 20.57m),
            MakeBar(SourceType.Tiingo, "CIFR", 20.47m));
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);
        await service.ReconcileAsync("CIFR", Day, Day, 1, CancellationToken.None);

        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.False(verification.Verified);
        Assert.Equal(1, verification.MatchedSourceCount);

        var discrepancy = await dbContext.Discrepancies.SingleAsync();
        Assert.True(discrepancy.PercentDiff > 0.001m);
    }

    [Fact]
    public async Task ReconcileAsync_AllBarsInGroupHaveZeroOrNegativeClose_SkipsGroupEntirelyWithoutThrowing()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawOhlcvBars.AddRange(
            MakeBar(SourceType.Alpaca, "AAPL", 0m),
            MakeBar(SourceType.Tiingo, "AAPL", -5m));
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);

        var exception = await Record.ExceptionAsync(() => service.ReconcileAsync("AAPL", Day, Day, 1, CancellationToken.None));

        Assert.Null(exception);
        Assert.Empty(dbContext.BarVerifications);
        Assert.Empty(dbContext.Discrepancies);
    }

    [Fact]
    public async Task ReconcileAsync_OhlAgreeButCloseDisagreesBeyondTolerance_ResolvesToTiingoCloseVerifiedTrue()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Open/High/Low agree closely (well within the hybrid tolerance); only Close
        // disagrees beyond it (~1% - fails both the 0.1% and $0.02 legs).
        dbContext.RawOhlcvBars.AddRange(
            MakeBarWithOhlc(SourceType.Alpaca, "AAPL", open: 200.05m, high: 205.02m, low: 199.00m, close: 200.00m),
            MakeBarWithOhlc(SourceType.Tiingo, "AAPL", open: 200.00m, high: 205.00m, low: 199.02m, close: 202.10m));
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);
        await service.ReconcileAsync("AAPL", Day, Day, 1, CancellationToken.None);

        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.True(verification.Verified);
        Assert.Equal(202.10m, verification.PrimarySourceValue); // Tiingo's Close, not Alpaca's 200.00

        var discrepancy = await dbContext.Discrepancies.SingleAsync();
        Assert.Equal(DiscrepancyResolutionMethod.OhlAgreedCloseResolvedToTiingo, discrepancy.ResolutionMethod);
    }

    [Fact]
    public async Task ReconcileAsync_HighAlsoDisagreesBeyondTolerance_FallsThroughToUnresolvedUnchanged()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Close disagrees (same shape as the OHL-agreed case above), but High also disagrees
        // beyond tolerance this time - the exception must not fire when any one of O/H/L also
        // diverges, regardless of how close Open/Low are.
        dbContext.RawOhlcvBars.AddRange(
            MakeBarWithOhlc(SourceType.Alpaca, "AAPL", open: 200.05m, high: 205.02m, low: 199.00m, close: 200.00m),
            MakeBarWithOhlc(SourceType.Tiingo, "AAPL", open: 200.00m, high: 215.00m, low: 199.02m, close: 202.10m));
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);
        await service.ReconcileAsync("AAPL", Day, Day, 1, CancellationToken.None);

        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.False(verification.Verified);
        Assert.Equal(200.00m, verification.PrimarySourceValue); // unchanged - still Alpaca's Close

        var discrepancy = await dbContext.Discrepancies.SingleAsync();
        Assert.Equal(DiscrepancyResolutionMethod.Unresolved, discrepancy.ResolutionMethod);
    }

    [Fact]
    public async Task ReconcileAsync_ExactMatchNoDisagreementAtAll_UnaffectedByTheNewException()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Every field identical between sources - no Discrepancy should ever be written, and
        // the new exception logic (which only ever runs inside the Close-disagreement branch)
        // must not change this.
        dbContext.RawOhlcvBars.AddRange(
            MakeBarWithOhlc(SourceType.Alpaca, "AAPL", open: 200.00m, high: 205.00m, low: 199.00m, close: 201.00m),
            MakeBarWithOhlc(SourceType.Tiingo, "AAPL", open: 200.00m, high: 205.00m, low: 199.00m, close: 201.00m));
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);
        await service.ReconcileAsync("AAPL", Day, Day, 1, CancellationToken.None);

        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.True(verification.Verified);
        Assert.Equal(201.00m, verification.PrimarySourceValue);

        Assert.Empty(dbContext.Discrepancies);
    }

    [Fact]
    public async Task ReconcileAsync_JpmJune26RealBarValues_ResolvesToTiingoCloseWithOhlAgreedFlag()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Real Alpaca/Tiingo bar values for JPM 2026-06-26, captured verbatim during this
        // session's discrepancy investigation (CLAUDE.md, "Reconciliation: Narrow Tiingo-Close
        // Exception..." block). Independently confirmed against a real published close
        // ($329.05, matching Tiingo exactly) during that investigation - this is the case the
        // whole exception exists to resolve correctly.
        dbContext.RawOhlcvBars.AddRange(
            MakeBarWithOhlc(
                SourceType.Alpaca, "JPM",
                open: 335.7550m, high: 336.2050m, low: 327.5000m, close: 327.5000m, volume: 406_122),
            MakeBarWithOhlc(
                SourceType.Tiingo, "JPM",
                open: 336.0000m, high: 336.4000m, low: 327.5000m, close: 329.0500m, volume: 17_640_907));
        await dbContext.SaveChangesAsync();

        var service = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);
        await service.ReconcileAsync("JPM", Day, Day, 1, CancellationToken.None);

        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.True(verification.Verified);
        Assert.Equal(329.0500m, verification.PrimarySourceValue);

        var discrepancy = await dbContext.Discrepancies.SingleAsync();
        Assert.Equal(DiscrepancyResolutionMethod.OhlAgreedCloseResolvedToTiingo, discrepancy.ResolutionMethod);
    }
}
