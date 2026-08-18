using Drps.Calculator.Verification;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace Drps.Tests.Calculator;

public class RvolVerificationJoinServiceTests
{
    private static readonly DateOnly FirstDay = new(2026, 1, 1);

    private static RawOhlcvBar MakeBar(string symbol, DateTimeOffset timestamp, long volume) => new()
    {
        Source = SourceType.Alpaca,
        Symbol = symbol,
        Timestamp = timestamp,
        Resolution = "1Day",
        Open = 99m,
        High = 101m,
        Low = 98m,
        Close = 100m,
        Volume = volume,
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
            dbContext.RawOhlcvBars.Add(MakeBar(symbol, timestamp, 1000L));
            dbContext.BarVerifications.Add(MakeVerification(symbol, timestamp, verified));
        }
    }

    [Fact]
    public async Task IsRvolVerifiedAsync_AllTwentyOneBarsInWindowVerified_ReturnsTrue()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 21, verified: true);
        await dbContext.SaveChangesAsync();

        var service = new RvolVerificationJoinService(dbContext);
        var result = await service.IsRvolVerifiedAsync("AAPL", FirstDay.AddDays(20), CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsRvolVerifiedAsync_OneUnverifiedBarInsideWindow_ReturnsFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 20, verified: true);

        // The 21st and final bar in the window is unverified - fail-closed means the whole
        // window is unverified, not just this one bar.
        var timestamp = new DateTimeOffset(FirstDay.AddDays(20).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 1000L));
        dbContext.BarVerifications.Add(MakeVerification("AAPL", timestamp, verified: false));
        await dbContext.SaveChangesAsync();

        var service = new RvolVerificationJoinService(dbContext);
        var result = await service.IsRvolVerifiedAsync("AAPL", FirstDay.AddDays(20), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsRvolVerifiedAsync_UnverifiedBarLaterBecomesVerified_NextCallReflectsNewStateWithNoCaching()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 20, verified: true);

        var timestamp = new DateTimeOffset(FirstDay.AddDays(20).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 1000L));
        var verification = MakeVerification("AAPL", timestamp, verified: false);
        dbContext.BarVerifications.Add(verification);
        await dbContext.SaveChangesAsync();

        var service = new RvolVerificationJoinService(dbContext);

        var beforeSecondSourceArrives = await service.IsRvolVerifiedAsync("AAPL", FirstDay.AddDays(20), CancellationToken.None);
        Assert.False(beforeSecondSourceArrives);

        // A second source catches up and BarReconciliationService flips this same row to
        // verified - simulated here directly, since reconciliation itself is out of scope.
        verification.Verified = true;
        verification.MatchedSourceCount = 2;
        await dbContext.SaveChangesAsync();

        var afterSecondSourceArrives = await service.IsRvolVerifiedAsync("AAPL", FirstDay.AddDays(20), CancellationToken.None);
        Assert.True(afterSecondSourceArrives);
    }

    [Fact]
    public async Task IsRvolVerifiedAsync_FewerThanTwentyOneBarsExist_ReturnsFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 20, verified: true); // one short of a 21-bar window

        var service = new RvolVerificationJoinService(dbContext);
        var result = await service.IsRvolVerifiedAsync("AAPL", FirstDay.AddDays(19), CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsRvolVerifiedAsync_BarWithNoVerificationRowAtAll_ReturnsFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 20, verified: true);

        // 21st bar exists but reconciliation hasn't run for it yet - no BarVerification row.
        var timestamp = new DateTimeOffset(FirstDay.AddDays(20).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 1000L));
        await dbContext.SaveChangesAsync();

        var service = new RvolVerificationJoinService(dbContext);
        var result = await service.IsRvolVerifiedAsync("AAPL", FirstDay.AddDays(20), CancellationToken.None);

        Assert.False(result);
    }
}
