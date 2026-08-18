using Drps.Calculator.Verification;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace Drps.Tests.Calculator;

public class RsiVerificationJoinServiceTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

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

    // Seeds `count` consecutive daily bars starting at FirstDay, each with its own
    // BarVerification row set to `verified`.
    private static void SeedBars(Drps.Calculator.Persistence.CalculatorDbContext dbContext, string symbol, int count, bool verified)
    {
        for (var i = 0; i < count; i++)
        {
            var timestamp = new DateTimeOffset(FirstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(MakeBar(symbol, timestamp, i + 1m));
            dbContext.BarVerifications.Add(MakeVerification(symbol, timestamp, verified));
        }
    }

    [Fact]
    public async Task IsRsiVerifiedAsync_AllFifteenBarsInWindowVerified_ReturnsTrue()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 15, verified: true);
        await dbContext.SaveChangesAsync();

        var service = new RsiVerificationJoinService(dbContext);
        var result = await service.IsRsiVerifiedAsync("AAPL", FirstDay.AddDays(14), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsRsiVerifiedAsync_OneUnverifiedBarInsideWindow_ReturnsFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 14, verified: true);

        // The 15th and final bar in the window is unverified - fail-closed means the whole
        // window is unverified, not just this one bar.
        var timestamp = new DateTimeOffset(FirstDay.AddDays(14).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 15m));
        dbContext.BarVerifications.Add(MakeVerification("AAPL", timestamp, verified: false));
        await dbContext.SaveChangesAsync();

        var service = new RsiVerificationJoinService(dbContext);
        var result = await service.IsRsiVerifiedAsync("AAPL", FirstDay.AddDays(14), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsRsiVerifiedAsync_UnverifiedBarLaterBecomesVerified_NextCallReflectsNewStateWithNoCaching()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 14, verified: true);

        var timestamp = new DateTimeOffset(FirstDay.AddDays(14).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 15m));
        var verification = MakeVerification("AAPL", timestamp, verified: false);
        dbContext.BarVerifications.Add(verification);
        await dbContext.SaveChangesAsync();

        var service = new RsiVerificationJoinService(dbContext);

        var beforeSecondSourceArrives = await service.IsRsiVerifiedAsync("AAPL", FirstDay.AddDays(14), CancellationToken.None);
        Assert.False(beforeSecondSourceArrives);

        // A second source catches up and BarReconciliationService flips this same row to
        // verified - simulated here directly, since reconciliation itself is out of scope.
        verification.Verified = true;
        verification.MatchedSourceCount = 2;
        await dbContext.SaveChangesAsync();

        var afterSecondSourceArrives = await service.IsRsiVerifiedAsync("AAPL", FirstDay.AddDays(14), CancellationToken.None);
        Assert.True(afterSecondSourceArrives);
    }

    [Fact]
    public async Task IsRsiVerifiedAsync_FewerThanFifteenBarsExist_ReturnsFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 14, verified: true); // one short of a 15-bar window

        var service = new RsiVerificationJoinService(dbContext);
        var result = await service.IsRsiVerifiedAsync("AAPL", FirstDay.AddDays(13), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsRsiVerifiedAsync_BarWithNoVerificationRowAtAll_ReturnsFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 14, verified: true);

        // 15th bar exists but reconciliation hasn't run for it yet - no BarVerification row.
        var timestamp = new DateTimeOffset(FirstDay.AddDays(14).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 15m));
        await dbContext.SaveChangesAsync();

        var service = new RsiVerificationJoinService(dbContext);
        var result = await service.IsRsiVerifiedAsync("AAPL", FirstDay.AddDays(14), CancellationToken.None);

        Assert.False(result);
    }
}
