using Drps.Calculator.Verification;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace Drps.Tests.Calculator;

public class DmaVerificationJoinServiceTests
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
    public async Task IsDmaVerifiedAsync_AllFiveBarsInWindowVerified_ReturnsTrue()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 5, verified: true);
        await dbContext.SaveChangesAsync();

        var service = new DmaVerificationJoinService(dbContext);
        var result = await service.IsDmaVerifiedAsync("AAPL", FirstDay.AddDays(4), 5, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task IsDmaVerifiedAsync_OneUnverifiedBarInsideWindow_ReturnsFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 4, verified: true);

        // The 5th and final bar in the window is unverified - fail-closed means the whole
        // window is unverified, not just this one bar.
        var timestamp = new DateTimeOffset(FirstDay.AddDays(4).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 5m));
        dbContext.BarVerifications.Add(MakeVerification("AAPL", timestamp, verified: false));
        await dbContext.SaveChangesAsync();

        var service = new DmaVerificationJoinService(dbContext);
        var result = await service.IsDmaVerifiedAsync("AAPL", FirstDay.AddDays(4), 5, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsDmaVerifiedAsync_UnverifiedBarLaterBecomesVerified_NextCallReflectsNewStateWithNoCaching()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 4, verified: true);

        var timestamp = new DateTimeOffset(FirstDay.AddDays(4).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 5m));
        var verification = MakeVerification("AAPL", timestamp, verified: false);
        dbContext.BarVerifications.Add(verification);
        await dbContext.SaveChangesAsync();

        var service = new DmaVerificationJoinService(dbContext);

        var beforeSecondSourceArrives = await service.IsDmaVerifiedAsync("AAPL", FirstDay.AddDays(4), 5, CancellationToken.None);
        Assert.False(beforeSecondSourceArrives);

        // A second source catches up and BarReconciliationService flips this same row to
        // verified - simulated here directly, since reconciliation itself is out of scope.
        verification.Verified = true;
        verification.MatchedSourceCount = 2;
        await dbContext.SaveChangesAsync();

        var afterSecondSourceArrives = await service.IsDmaVerifiedAsync("AAPL", FirstDay.AddDays(4), 5, CancellationToken.None);
        Assert.True(afterSecondSourceArrives);
    }

    [Fact]
    public async Task IsDmaVerifiedAsync_FewerThanWindowBarsExist_ReturnsFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 4, verified: true); // one short of a 5-bar window

        var service = new DmaVerificationJoinService(dbContext);
        var result = await service.IsDmaVerifiedAsync("AAPL", FirstDay.AddDays(3), 5, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task IsDmaVerifiedAsync_BarWithNoVerificationRowAtAll_ReturnsFalse()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 4, verified: true);

        // 5th bar exists but reconciliation hasn't run for it yet - no BarVerification row.
        var timestamp = new DateTimeOffset(FirstDay.AddDays(4).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        dbContext.RawOhlcvBars.Add(MakeBar("AAPL", timestamp, 5m));
        await dbContext.SaveChangesAsync();

        var service = new DmaVerificationJoinService(dbContext);
        var result = await service.IsDmaVerifiedAsync("AAPL", FirstDay.AddDays(4), 5, CancellationToken.None);

        Assert.False(result);
    }

    // Blast-radius scoping (CLAUDE.md 2026-08-04) - the three scenarios that decision's own
    // implementation task requires, proving IsDmaVerifiedAsync's per-window independence
    // directly rather than only through GateQualityScorer/GateScanService's higher-level
    // behavior. Each seeds a genuine 60-bar window (SeedBars, i=0 oldest through i=59 most
    // recent) and calls the method once per real DmaCalculator window size (5/15/30/60)
    // against the same fixture, so all four results come from one consistent bar history.

    [Fact]
    public async Task IsDmaVerifiedAsync_BadBarOnlyInDays31To60_VerifiesDma5Dma15Dma30ButNotDma60()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 60, verified: true);

        // i=15 is rank 45 counting back from the most recent bar (i=59) - squarely inside
        // days 31-60, outside every narrower window's own lookback. Matches this decision's
        // own worked example (a bad bar 45 trading days old).
        var badBarTimestamp = new DateTimeOffset(FirstDay.AddDays(15).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        await dbContext.SaveChangesAsync();
        var badBarVerification = await dbContext.BarVerifications.SingleAsync(v => v.Timestamp == badBarTimestamp);
        badBarVerification.Verified = false;
        await dbContext.SaveChangesAsync();

        var service = new DmaVerificationJoinService(dbContext);
        var mostRecentBarDate = FirstDay.AddDays(59);

        Assert.True(await service.IsDmaVerifiedAsync("AAPL", mostRecentBarDate, 5, CancellationToken.None));
        Assert.True(await service.IsDmaVerifiedAsync("AAPL", mostRecentBarDate, 15, CancellationToken.None));
        Assert.True(await service.IsDmaVerifiedAsync("AAPL", mostRecentBarDate, 30, CancellationToken.None));
        Assert.False(await service.IsDmaVerifiedAsync("AAPL", mostRecentBarDate, 60, CancellationToken.None));
    }

    [Fact]
    public async Task IsDmaVerifiedAsync_BadBarInDays1To5_FailsAllFourWindows()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 60, verified: true);

        // i=57 is rank 3 counting back from the most recent bar (i=59) - inside every
        // window's own lookback, since narrower windows are always a bar-count subset of
        // wider ones ending at the same anchor date.
        var badBarTimestamp = new DateTimeOffset(FirstDay.AddDays(57).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        await dbContext.SaveChangesAsync();
        var badBarVerification = await dbContext.BarVerifications.SingleAsync(v => v.Timestamp == badBarTimestamp);
        badBarVerification.Verified = false;
        await dbContext.SaveChangesAsync();

        var service = new DmaVerificationJoinService(dbContext);
        var mostRecentBarDate = FirstDay.AddDays(59);

        Assert.False(await service.IsDmaVerifiedAsync("AAPL", mostRecentBarDate, 5, CancellationToken.None));
        Assert.False(await service.IsDmaVerifiedAsync("AAPL", mostRecentBarDate, 15, CancellationToken.None));
        Assert.False(await service.IsDmaVerifiedAsync("AAPL", mostRecentBarDate, 30, CancellationToken.None));
        Assert.False(await service.IsDmaVerifiedAsync("AAPL", mostRecentBarDate, 60, CancellationToken.None));
    }

    [Fact]
    public async Task IsDmaVerifiedAsync_ZeroBadBars_VerifiesAllFourWindows()
    {
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();
        SeedBars(dbContext, "AAPL", 60, verified: true);
        await dbContext.SaveChangesAsync();

        var service = new DmaVerificationJoinService(dbContext);
        var mostRecentBarDate = FirstDay.AddDays(59);

        Assert.True(await service.IsDmaVerifiedAsync("AAPL", mostRecentBarDate, 5, CancellationToken.None));
        Assert.True(await service.IsDmaVerifiedAsync("AAPL", mostRecentBarDate, 15, CancellationToken.None));
        Assert.True(await service.IsDmaVerifiedAsync("AAPL", mostRecentBarDate, 30, CancellationToken.None));
        Assert.True(await service.IsDmaVerifiedAsync("AAPL", mostRecentBarDate, 60, CancellationToken.None));
    }
}
