using Drps.Calculator.Rsi;
using Drps.Calculator.Verification;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace Drps.Tests.Calculator;

public class RsiSlopeVerificationJoinServiceTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);
    private const int Lookback = 3;

    private static RawOhlcvBar MakeBar(string symbol, DateTimeOffset timestamp, decimal close) => new()
    {
        Source = SourceType.Alpaca,
        Symbol = symbol,
        Timestamp = timestamp,
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

    private static BarVerification MakeVerification(string symbol, DateTimeOffset timestamp, bool verified) => new()
    {
        Symbol = symbol,
        Timestamp = timestamp,
        Resolution = "1Day",
        SourceCount = verified ? 2 : 1,
        MatchedSourceCount = verified ? 2 : 1,
        Verified = verified,
        ToleranceApplied = 0.001m,
        ComputationVersion = 1,
        EvaluatedAt = DateTimeOffset.UtcNow
    };

    // Seeds `count` consecutive daily bars starting at FirstDay (day index 0..count-1), each
    // with its own BarVerification row set to `verified` - same shape as
    // RsiVerificationJoinServiceTests/DmaVerificationJoinServiceTests.
    private static void SeedBars(Drps.Calculator.Persistence.CalculatorDbContext dbContext, string symbol, int count, bool verified)
    {
        for (var i = 0; i < count; i++)
        {
            var timestamp = new DateTimeOffset(FirstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar(symbol, timestamp, i + 1m));
            dbContext.BarVerifications.Add(MakeVerification(symbol, timestamp, verified));
        }
    }

    // Seeds one RsiIndicator row per given day index (relative to FirstDay), current
    // CalculationVersion - the real persisted row sequence IsRsiSlopeVerifiedAsync resolves
    // its "lookback positions back" endpoint from.
    private static void SeedRsiRows(Drps.Calculator.Persistence.CalculatorDbContext dbContext, string symbol, params int[] dayIndexes)
    {
        foreach (var dayIndex in dayIndexes)
        {
            dbContext.RsiIndicators.Add(new RsiIndicator
            {
                Symbol = symbol,
                BarDate = FirstDay.AddDays(dayIndex),
                Period = 14,
                Value = 50m,
                HasExDividendEvent = false,
                HasTiingoCorrectedClose = false,
                VerificationScopeLimited = true,
                TickerSourceOrigin = TickerSourceOrigin.Watchlist,
                CalculationVersion = RsiComputationService.CalculationVersion,
                ComputedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private static RsiSlopeVerificationJoinService CreateService(Drps.Calculator.Persistence.CalculatorDbContext dbContext) =>
        new(dbContext, new RsiVerificationJoinService(dbContext));

    [Fact]
    public async Task IsRsiSlopeVerifiedAsync_BothEndpointsClean_ReturnsTrue()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // Near endpoint (day 17) needs bars [3,17] verified; far endpoint (day 17-3=14) needs
        // bars [0,14] verified - union is the full [0,17] 18-bar span (15 + lookback).
        SeedBars(dbContext, "AAPL", count: 18, verified: true);
        SeedRsiRows(dbContext, "AAPL", 14, 15, 16, 17);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var result = await service.IsRsiSlopeVerifiedAsync("AAPL", FirstDay.AddDays(17), Lookback, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsRsiSlopeVerifiedAsync_NearEndpointBadBar_ReturnsFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", count: 18, verified: true);
        SeedRsiRows(dbContext, "AAPL", 14, 15, 16, 17);
        await dbContext.SaveChangesAsync();

        // Day 16 is inside the near endpoint's own window ([3,17]) but outside the far
        // endpoint's ([0,14]) - proves the near-endpoint check alone is what fails this,
        // short-circuiting before the far endpoint is ever consulted.
        var badTimestamp = new DateTimeOffset(FirstDay.AddDays(16).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var verification = await dbContext.BarVerifications.SingleAsync(v => v.Symbol == "AAPL" && v.Timestamp == badTimestamp);
        verification.Verified = false;
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var result = await service.IsRsiSlopeVerifiedAsync("AAPL", FirstDay.AddDays(17), Lookback, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsRsiSlopeVerifiedAsync_FarEndpointBadBar_ReturnsFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", count: 18, verified: true);
        SeedRsiRows(dbContext, "AAPL", 14, 15, 16, 17);
        await dbContext.SaveChangesAsync();

        // Day 2 is inside the far endpoint's own window ([0,14]) but outside the near
        // endpoint's ([3,17]) - the near-endpoint check alone passes, so this proves the far
        // endpoint is genuinely, independently checked rather than assumed clean once the
        // near endpoint verifies.
        var badTimestamp = new DateTimeOffset(FirstDay.AddDays(2).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var verification = await dbContext.BarVerifications.SingleAsync(v => v.Symbol == "AAPL" && v.Timestamp == badTimestamp);
        verification.Verified = false;
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var result = await service.IsRsiSlopeVerifiedAsync("AAPL", FirstDay.AddDays(17), Lookback, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsRsiSlopeVerifiedAsync_InsufficientRsiHistoryToResolveLookbackRow_ReturnsFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // Bars are fully clean - the point of this test is that the RSI *row* history is too
        // short to resolve the lookback-back endpoint at all (only 3 RsiIndicator rows exist
        // on or before the test date, one short of the lookback+1=4 needed), independent of
        // whether the underlying bars would otherwise verify.
        SeedBars(dbContext, "AAPL", count: 18, verified: true);
        SeedRsiRows(dbContext, "AAPL", 15, 16, 17); // day 14 missing - a real RSI-series gap

        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var result = await service.IsRsiSlopeVerifiedAsync("AAPL", FirstDay.AddDays(17), Lookback, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsRsiSlopeVerifiedAsync_NoBarVerificationRowAtAllForOneContributingBar_ReturnsFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        // Seed 17 verified bars (day 0..16); day 17's bar exists with no BarVerification row
        // at all yet (reconciliation hasn't run for it), distinct from an explicit
        // Verified=false row.
        SeedBars(dbContext, "AAPL", count: 17, verified: true);
        var lastTimestamp = new DateTimeOffset(FirstDay.AddDays(17).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", lastTimestamp, 18m));
        SeedRsiRows(dbContext, "AAPL", 14, 15, 16, 17);
        await dbContext.SaveChangesAsync();

        var service = CreateService(dbContext);
        var result = await service.IsRsiSlopeVerifiedAsync("AAPL", FirstDay.AddDays(17), Lookback, CancellationToken.None);

        Assert.False(result);
    }
}
