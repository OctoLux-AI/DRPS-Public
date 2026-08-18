using Drps.Ingestion.Persistence;
using Drps.Ledger;
using Drps.Shared.Models;
using Drps.Shared.Notifications;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Ledger;

public class LedgerLifecycleStampServiceTests
{
    private static readonly DateTime EntryDate = new(2026, 7, 10, 9, 30, 0);
    private static readonly DateTime AsOf = new(2026, 7, 18, 12, 0, 0);

    // Records every call it receives rather than asserting real notification behavior - this
    // task only requires proving the seam is invoked, per the 2026-07-18 decision block's
    // point 6 ("verify via a test double... without asserting any real notification behavior").
    private class RecordingLifecycleNotificationService : ILifecycleNotificationService
    {
        public List<LifecycleStampNotification> Notifications { get; } = new();

        public Task NotifyLifecycleStampAsync(LifecycleStampNotification notification, CancellationToken cancellationToken)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }
    }

    private static async Task<Position> SeedOpenPositionAsync(DrpsDbContext dbContext, string ticker)
    {
        var gateScore = new GateScore
        {
            Ticker = ticker,
            Bucket = GateBucket.Buy,
            CompositeScore = 0.90m,
            ScanDate = EntryDate,
            CalculationVersion = 1,
            GateParameterVersion = 1
        };
        dbContext.GateScores.Add(gateScore);
        await dbContext.SaveChangesAsync();

        var allocation = new AdjusterAllocation
        {
            GateScoreId = gateScore.Id,
            AllocationPercent = 0.03m,
            AllocationDollarAmount = 30000m,
            ShareCount = 300,
            ShareCapDeficient = false,
            AsOfTimestamp = EntryDate,
            AdjusterParameterVersion = 1
        };
        dbContext.AdjusterAllocations.Add(allocation);
        await dbContext.SaveChangesAsync();

        var position = new Position
        {
            Ticker = ticker,
            GateScoreId = gateScore.Id,
            AdjusterAllocationId = allocation.Id,
            EntryDate = EntryDate,
            EntryPrice = 100m,
            EntryQuantity = 300m
        };
        dbContext.Positions.Add(position);
        await dbContext.SaveChangesAsync();

        return position;
    }

    private static LedgerLifecycleStampService CreateService(
        DrpsDbContext dbContext,
        ILifecycleNotificationService? notificationService = null,
        ILogger<LedgerLifecycleStampService>? logger = null)
    {
        return new LedgerLifecycleStampService(
            dbContext,
            notificationService ?? new NoOpLifecycleNotificationService(),
            logger ?? NullLogger<LedgerLifecycleStampService>.Instance);
    }

    [Fact]
    public async Task StampCompositeDegradationAsync_OpenPositionExists_StampsLowGradeDateAndCoolDownStartDateTogether()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var position = await SeedOpenPositionAsync(dbContext, "AAA");

        var service = CreateService(dbContext);
        await service.StampCompositeDegradationAsync("AAA", AsOf, CancellationToken.None);

        var stamped = await dbContext.Positions.SingleAsync(p => p.Id == position.Id);
        Assert.Equal(AsOf, stamped.LowGradeDate);
        Assert.Equal(AsOf, stamped.CoolDownStartDate);

        // Untouched - Gate never writes these, per Ledger's manual-execution-only rule.
        Assert.Null(stamped.ExitDate);
        Assert.Null(stamped.ExitPrice);
        Assert.Null(stamped.ExitQuantity);
        Assert.Null(stamped.ExitReason);
    }

    [Fact]
    public async Task StampCompositeDegradationAsync_NoMatchingOpenPosition_NoOpsAndLogsWithoutThrowing()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // A closed position for this ticker exists, but no open one - must not be mistaken
        // for a match (lookup key is (Ticker, ExitDate == null), not ticker alone).
        var closedPosition = await SeedOpenPositionAsync(dbContext, "BBB");
        closedPosition.ExitDate = EntryDate.AddDays(3);
        closedPosition.ExitPrice = 90m;
        closedPosition.ExitQuantity = 300m;
        closedPosition.ExitReason = PositionExitReason.AtrStop;
        await dbContext.SaveChangesAsync();

        var capturingLogger = new CapturingLogger<LedgerLifecycleStampService>();
        var service = CreateService(dbContext, logger: capturingLogger);

        // Must not throw - silent no-op is the documented failure mode.
        await service.StampCompositeDegradationAsync("BBB", AsOf, CancellationToken.None);

        var unchanged = await dbContext.Positions.SingleAsync(p => p.Id == closedPosition.Id);
        Assert.Null(unchanged.LowGradeDate);
        Assert.Null(unchanged.CoolDownStartDate);
        Assert.True(capturingLogger.WarningCount > 0);
    }

    [Fact]
    public async Task StampCompositeDegradationAsync_TickerNeverHadAPosition_NoOpsAndLogsWithoutThrowing()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var capturingLogger = new CapturingLogger<LedgerLifecycleStampService>();
        var service = CreateService(dbContext, logger: capturingLogger);

        await service.StampCompositeDegradationAsync("ZZZ", AsOf, CancellationToken.None);

        Assert.Empty(await dbContext.Positions.ToListAsync());
        Assert.True(capturingLogger.WarningCount > 0);
    }

    [Fact]
    public async Task StampCompositeDegradationAsync_OpenPositionExists_CallsNotificationServiceExactlyOnce()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var position = await SeedOpenPositionAsync(dbContext, "CCC");

        var recordingNotificationService = new RecordingLifecycleNotificationService();
        var service = CreateService(dbContext, notificationService: recordingNotificationService);

        await service.StampCompositeDegradationAsync("CCC", AsOf, CancellationToken.None);

        var notification = Assert.Single(recordingNotificationService.Notifications);
        Assert.Equal("CCC", notification.Ticker);
        Assert.Equal(position.Id, notification.PositionId);
        Assert.Equal(AsOf, notification.AsOf);
    }

    [Fact]
    public async Task StampCompositeDegradationAsync_NoMatchingOpenPosition_DoesNotCallNotificationService()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var recordingNotificationService = new RecordingLifecycleNotificationService();
        var service = CreateService(dbContext, notificationService: recordingNotificationService);

        await service.StampCompositeDegradationAsync("ZZZ", AsOf, CancellationToken.None);

        Assert.Empty(recordingNotificationService.Notifications);
    }

    // CLAUDE.md's "Adjuster: Concurrent-Position-Cap Displacement, 10% Relative Composite-
    // Score Margin" (2026-08-01) - StampPositionCountDisplacementAsync tests below, same
    // shape as StampCompositeDegradationAsync's own tests above.

    [Fact]
    public async Task StampPositionCountDisplacementAsync_OpenPositionExists_StampsDisplacementDateOnly()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var position = await SeedOpenPositionAsync(dbContext, "AAA");

        var service = CreateService(dbContext);
        await service.StampPositionCountDisplacementAsync("AAA", AsOf, CancellationToken.None);

        var stamped = await dbContext.Positions.SingleAsync(p => p.Id == position.Id);
        Assert.Equal(AsOf, stamped.DisplacementDate);

        // The defining difference from StampCompositeDegradationAsync - CoolDownStartDate
        // must stay untouched, per the locked design decision's explicit requirement.
        // NoBuy-list whipsaw protection is specific to composite-degradation exits, not
        // position-count displacement.
        Assert.Null(stamped.CoolDownStartDate);
        Assert.Null(stamped.LowGradeDate);

        // Untouched - Adjuster never writes these, same rule as Gate.
        Assert.Null(stamped.ExitDate);
        Assert.Null(stamped.ExitPrice);
        Assert.Null(stamped.ExitQuantity);
        Assert.Null(stamped.ExitReason);
    }

    [Fact]
    public async Task StampPositionCountDisplacementAsync_NoMatchingOpenPosition_NoOpsAndLogsWithoutThrowing()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var closedPosition = await SeedOpenPositionAsync(dbContext, "BBB");
        closedPosition.ExitDate = EntryDate.AddDays(3);
        closedPosition.ExitPrice = 90m;
        closedPosition.ExitQuantity = 300m;
        closedPosition.ExitReason = PositionExitReason.AtrStop;
        await dbContext.SaveChangesAsync();

        var capturingLogger = new CapturingLogger<LedgerLifecycleStampService>();
        var service = CreateService(dbContext, logger: capturingLogger);

        await service.StampPositionCountDisplacementAsync("BBB", AsOf, CancellationToken.None);

        var unchanged = await dbContext.Positions.SingleAsync(p => p.Id == closedPosition.Id);
        Assert.Null(unchanged.DisplacementDate);
        Assert.True(capturingLogger.WarningCount > 0);
    }

    [Fact]
    public async Task StampPositionCountDisplacementAsync_TickerNeverHadAPosition_NoOpsAndLogsWithoutThrowing()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var capturingLogger = new CapturingLogger<LedgerLifecycleStampService>();
        var service = CreateService(dbContext, logger: capturingLogger);

        await service.StampPositionCountDisplacementAsync("ZZZ", AsOf, CancellationToken.None);

        Assert.Empty(await dbContext.Positions.ToListAsync());
        Assert.True(capturingLogger.WarningCount > 0);
    }

    [Fact]
    public async Task StampPositionCountDisplacementAsync_OpenPositionExists_CallsNotificationServiceExactlyOnceWithCorrectReason()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var position = await SeedOpenPositionAsync(dbContext, "CCC");

        var recordingNotificationService = new RecordingLifecycleNotificationService();
        var service = CreateService(dbContext, notificationService: recordingNotificationService);

        await service.StampPositionCountDisplacementAsync("CCC", AsOf, CancellationToken.None);

        var notification = Assert.Single(recordingNotificationService.Notifications);
        Assert.Equal("CCC", notification.Ticker);
        Assert.Equal(position.Id, notification.PositionId);
        Assert.Equal(AsOf, notification.AsOf);
        Assert.Equal("PositionCountDisplacement", notification.StampReason);
    }

    [Fact]
    public async Task StampPositionCountDisplacementAsync_NoMatchingOpenPosition_DoesNotCallNotificationService()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var recordingNotificationService = new RecordingLifecycleNotificationService();
        var service = CreateService(dbContext, notificationService: recordingNotificationService);

        await service.StampPositionCountDisplacementAsync("ZZZ", AsOf, CancellationToken.None);

        Assert.Empty(recordingNotificationService.Notifications);
    }

    // Tracks log levels - only WarningCount is needed by these tests, same minimal shape as
    // GateScanServiceTests' own CapturingLogger (which tracks CriticalCount instead).
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<LogLevel> _levels = new();

        public int WarningCount => _levels.Count(l => l == LogLevel.Warning);

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _levels.Add(logLevel);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
