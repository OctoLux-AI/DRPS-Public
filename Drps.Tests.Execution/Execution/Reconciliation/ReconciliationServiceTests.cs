using Drps.Execution.Alpaca;
using Drps.Execution.Reconciliation;
using Drps.Ingestion.Persistence;
using Drps.Ledger;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Execution.Reconciliation;

public class ReconciliationServiceTests
{
    private static readonly DateTime AsOf = new(2026, 7, 21, 10, 0, 0);

    private static GateScore BuildGateScore(string ticker) => new()
    {
        Ticker = ticker,
        Bucket = GateBucket.Buy,
        CompositeScore = 0.90m,
        ScanDate = AsOf,
        CalculationVersion = 1,
        GateParameterVersion = 1
    };

    private static AdjusterAllocation BuildAllocation(long gateScoreId) => new()
    {
        GateScoreId = gateScoreId,
        AllocationPercent = 0.03m,
        AllocationDollarAmount = 1500m,
        ShareCount = 10m,
        ShareCapDeficient = false,
        AsOfTimestamp = AsOf,
        AdjusterParameterVersion = 1
    };

    private static async Task<Position> SeedOpenPositionAsync(
        DrpsDbContext dbContext, string ticker, decimal entryQuantity = 10m, decimal entryPrice = 150m)
    {
        var gateScore = BuildGateScore(ticker);
        dbContext.GateScores.Add(gateScore);
        await dbContext.SaveChangesAsync();

        var allocation = BuildAllocation(gateScore.Id);
        dbContext.AdjusterAllocations.Add(allocation);
        await dbContext.SaveChangesAsync();

        var writer = new LedgerPositionWriter(dbContext);
        return await writer.OpenPositionAsync(
            gateScore.Id, allocation.Id, ticker, AsOf, entryPrice, entryQuantity, CancellationToken.None,
            PositionActionOrigin.Automated);
    }

    private static ReconciliationService CreateService(
        DrpsDbContext dbContext, FakeAlpacaTradingClient client, Func<DateTime>? nowProvider = null,
        FakePushoverNotificationService? pushoverNotificationService = null) =>
        new(
            dbContext,
            client,
            new LedgerPositionWriter(dbContext),
            pushoverNotificationService ?? new FakePushoverNotificationService(),
            NullLogger<ReconciliationService>.Instance,
            nowProvider: nowProvider ?? (() => AsOf));

    private static AlpacaPosition BuildAlpacaPosition(string ticker, decimal quantity, decimal avgEntryPrice) => new()
    {
        Symbol = ticker,
        Quantity = quantity,
        AverageEntryPrice = avgEntryPrice,
        MarketValue = quantity * avgEntryPrice,
        Side = "long"
    };

    // --- Orphans (Alpaca minus Ledger) --------------------------------------------------------

    [Fact]
    public async Task RunAsync_NewOrphanAlpacaPosition_IsExcludedAndLogged()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var client = new FakeAlpacaTradingClient
        {
            PositionsResult = _ => Task.FromResult(new AlpacaPositionsResult
            {
                Success = true,
                Positions = new[] { BuildAlpacaPosition("ZZZZ", 5m, 20m) }
            })
        };
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(dbContext, client, pushoverNotificationService: pushover);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.NewOrphansDetected);
        Assert.Equal(0, result.AlreadyKnownOrphans);

        var excluded = await dbContext.ExcludedTickers.SingleAsync();
        Assert.Equal("ZZZZ", excluded.Ticker);
        Assert.False(string.IsNullOrWhiteSpace(excluded.Reason));

        // CLAUDE.md's Execution Layer: Ninth Design Decision - orphan detection is one of
        // Pushover's three wired trigger points.
        var notification = Assert.Single(pushover.SentMessages);
        Assert.Contains("ZZZZ", notification);
        Assert.Contains("orphan", notification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NEW", notification);
    }

    [Fact]
    public async Task RunAsync_RepeatOrphanDetection_DoesNotThrowOrDuplicate()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.ExcludedTickers.Add(new ExcludedTicker
        {
            Ticker = "ZZZZ",
            Reason = "Previously detected orphan.",
            CreatedDate = AsOf.AddDays(-1)
        });
        await dbContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient
        {
            PositionsResult = _ => Task.FromResult(new AlpacaPositionsResult
            {
                Success = true,
                Positions = new[] { BuildAlpacaPosition("ZZZZ", 5m, 20m) }
            })
        };
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(dbContext, client, pushoverNotificationService: pushover);

        // Run reconciliation twice - simulates two scheduled runs finding the same orphan.
        var firstRun = await service.RunAsync(CancellationToken.None);
        var secondRun = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, firstRun.NewOrphansDetected);
        Assert.Equal(1, firstRun.AlreadyKnownOrphans);
        Assert.Equal(0, secondRun.NewOrphansDetected);
        Assert.Equal(1, secondRun.AlreadyKnownOrphans);

        // Still exactly one row - no duplicate-key error, no duplicate insert.
        Assert.Equal(1, await dbContext.ExcludedTickers.CountAsync(e => e.Ticker == "ZZZZ"));

        // Still notified both times - this class's own LogCritical also re-fires on every
        // repeat detection (see ReconciliationService.HandleOrphanAsync's alreadyExcluded
        // branch), so the notification mirrors that existing behavior rather than
        // introducing a new dedup policy not asked for by this task.
        Assert.Equal(2, pushover.SentMessages.Count);
    }

    // --- Phantoms (Ledger minus Alpaca) --------------------------------------------------------

    [Fact]
    public async Task RunAsync_PhantomWithFilledSellOrderInHistory_IsAutoHealedWithCorrectFields()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var position = await SeedOpenPositionAsync(dbContext, "AAPL", entryQuantity: 10m, entryPrice: 150m);

        var client = new FakeAlpacaTradingClient
        {
            PositionsResult = _ => Task.FromResult(new AlpacaPositionsResult { Success = true }),
            OrdersBySymbolResult = (_, _) => Task.FromResult(new AlpacaOrdersResult
            {
                Success = true,
                Orders = new[]
                {
                    // The original entry fill - must NOT be mistaken for a closing order.
                    new AlpacaOrder
                    {
                        OrderId = "order-open", ClientOrderId = "drps-open-1", Symbol = "AAPL", Side = "buy",
                        Status = "filled", FilledQuantity = 10m, FilledAveragePrice = 150m,
                        FilledAt = AsOf.AddDays(-3)
                    },
                    // The real closing fill.
                    new AlpacaOrder
                    {
                        OrderId = "order-close", ClientOrderId = "drps-close-1", Symbol = "AAPL", Side = "sell",
                        Status = "filled", FilledQuantity = 10m, FilledAveragePrice = 175.50m,
                        FilledAt = AsOf.AddDays(-1)
                    }
                }
            })
        };
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(dbContext, client, pushoverNotificationService: pushover);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.PhantomsHealed);
        Assert.Equal(0, result.PhantomsUnresolved);

        var closed = await dbContext.Positions.SingleAsync(p => p.Id == position.Id);
        Assert.NotNull(closed.ExitDate);
        Assert.Equal(175.50m, closed.ExitPrice);
        Assert.Equal(10m, closed.ExitQuantity);
        Assert.Equal(PositionExitReason.ReconciliationHealed, closed.ExitReason);

        // Phantom auto-heal is also notification-worthy - the same LogCritical call site the
        // notification mirrors.
        var notification = Assert.Single(pushover.SentMessages);
        Assert.Contains("AAPL", notification);
        Assert.Contains("auto-healed", notification, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_PhantomWithOnlyCanceledOrExpiredOrders_IsNotHealed()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var position = await SeedOpenPositionAsync(dbContext, "AAPL");

        var client = new FakeAlpacaTradingClient
        {
            PositionsResult = _ => Task.FromResult(new AlpacaPositionsResult { Success = true }),
            OrdersBySymbolResult = (_, _) => Task.FromResult(new AlpacaOrdersResult
            {
                Success = true,
                Orders = new[]
                {
                    new AlpacaOrder
                    {
                        OrderId = "order-1", ClientOrderId = "drps-1", Symbol = "AAPL", Side = "sell",
                        Status = "canceled", FilledQuantity = null, FilledAveragePrice = null
                    },
                    new AlpacaOrder
                    {
                        OrderId = "order-2", ClientOrderId = "drps-2", Symbol = "AAPL", Side = "sell",
                        Status = "expired", FilledQuantity = null, FilledAveragePrice = null
                    }
                }
            })
        };
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(dbContext, client, pushoverNotificationService: pushover);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.PhantomsHealed);
        Assert.Equal(1, result.PhantomsUnresolved);

        var stillOpen = await dbContext.Positions.SingleAsync(p => p.Id == position.Id);
        Assert.Null(stillOpen.ExitDate);

        // Phantom-unresolved (no genuinely filled closing order found) is also
        // notification-worthy.
        var notification = Assert.Single(pushover.SentMessages);
        Assert.Contains("AAPL", notification);
        Assert.Contains("phantom", notification, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_PhantomWithNoMatchingOrderAtAll_IsNotHealed()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var position = await SeedOpenPositionAsync(dbContext, "AAPL");

        var client = new FakeAlpacaTradingClient
        {
            PositionsResult = _ => Task.FromResult(new AlpacaPositionsResult { Success = true }),
            OrdersBySymbolResult = (_, _) => Task.FromResult(new AlpacaOrdersResult { Success = true })
        };
        var service = CreateService(dbContext, client);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.PhantomsHealed);
        Assert.Equal(1, result.PhantomsUnresolved);

        var stillOpen = await dbContext.Positions.SingleAsync(p => p.Id == position.Id);
        Assert.Null(stillOpen.ExitDate);
    }

    [Fact]
    public async Task RunAsync_PhantomOrderHistoryOnlyHasFilledBuyOrder_IsNotMistakenForAClose()
    {
        // A filled BUY order (the entry itself) proves nothing about a close - only a filled
        // SELL counts as evidence. Confirms the Side filter, not just the Status filter.
        using var dbContext = InMemoryDbContextFactory.Create();
        var position = await SeedOpenPositionAsync(dbContext, "AAPL", entryQuantity: 10m, entryPrice: 150m);

        var client = new FakeAlpacaTradingClient
        {
            PositionsResult = _ => Task.FromResult(new AlpacaPositionsResult { Success = true }),
            OrdersBySymbolResult = (_, _) => Task.FromResult(new AlpacaOrdersResult
            {
                Success = true,
                Orders = new[]
                {
                    new AlpacaOrder
                    {
                        OrderId = "order-open", ClientOrderId = "drps-open-1", Symbol = "AAPL", Side = "buy",
                        Status = "filled", FilledQuantity = 10m, FilledAveragePrice = 150m, FilledAt = AsOf.AddDays(-3)
                    }
                }
            })
        };
        var service = CreateService(dbContext, client);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.PhantomsHealed);
        Assert.Equal(1, result.PhantomsUnresolved);

        var stillOpen = await dbContext.Positions.SingleAsync(p => p.Id == position.Id);
        Assert.Null(stillOpen.ExitDate);
    }

    // --- Intersection ---------------------------------------------------------------------------

    [Fact]
    public async Task RunAsync_IntersectionWithinTolerance_ProducesNoDiscrepancy()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedOpenPositionAsync(dbContext, "MSFT", entryQuantity: 10m, entryPrice: 300.00m);

        var client = new FakeAlpacaTradingClient
        {
            PositionsResult = _ => Task.FromResult(new AlpacaPositionsResult
            {
                Success = true,
                // Within both tolerances: quantity delta 0.0000005 <= 0.000001, price delta 0.005 <= 0.01.
                Positions = new[] { BuildAlpacaPosition("MSFT", 10.0000005m, 300.005m) }
            })
        };
        var service = CreateService(dbContext, client);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(0, result.DiscrepanciesLogged);
        Assert.Empty(await dbContext.PositionReconciliationDiscrepancies.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_IntersectionOutsideTolerance_ProducesExactlyOneDiscrepancyRowWithCorrectValues()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var position = await SeedOpenPositionAsync(dbContext, "MSFT", entryQuantity: 10m, entryPrice: 300.00m);

        var client = new FakeAlpacaTradingClient
        {
            PositionsResult = _ => Task.FromResult(new AlpacaPositionsResult
            {
                Success = true,
                Positions = new[] { BuildAlpacaPosition("MSFT", 9m, 310m) }
            })
        };
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(dbContext, client, pushoverNotificationService: pushover);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.DiscrepanciesLogged);

        var discrepancy = await dbContext.PositionReconciliationDiscrepancies.SingleAsync();
        Assert.Equal(position.Id, discrepancy.PositionId);
        Assert.Equal("MSFT", discrepancy.Ticker);
        Assert.Equal(10m, discrepancy.LedgerQuantity);
        Assert.Equal(9m, discrepancy.AlpacaQuantity);
        Assert.Equal(300.00m, discrepancy.LedgerPrice);
        Assert.Equal(310m, discrepancy.AlpacaAverageEntryPrice);

        // An intersection quantity/price mismatch is a third, distinct case from orphan/phantom
        // (CheckIntersection, not HandleOrphanAsync/HandlePhantomAsync) - not one of Pushover's
        // three wired trigger points per this task's explicit "orphan/phantom" scoping.
        Assert.Empty(pushover.SentMessages);
    }

    [Fact]
    public async Task RunAsync_IntersectionMismatch_NeverModifiesPositionRowItself()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var position = await SeedOpenPositionAsync(dbContext, "MSFT", entryQuantity: 10m, entryPrice: 300.00m);

        var client = new FakeAlpacaTradingClient
        {
            PositionsResult = _ => Task.FromResult(new AlpacaPositionsResult
            {
                Success = true,
                Positions = new[] { BuildAlpacaPosition("MSFT", 9m, 310m) }
            })
        };
        var service = CreateService(dbContext, client);

        await service.RunAsync(CancellationToken.None);

        var unchanged = await dbContext.Positions.AsNoTracking().SingleAsync(p => p.Id == position.Id);
        Assert.Equal(10m, unchanged.EntryQuantity);
        Assert.Equal(300.00m, unchanged.EntryPrice);
        Assert.Null(unchanged.ExitDate);
        Assert.Null(unchanged.ExitReason);
    }

    // --- Top-level fetch failure ---------------------------------------------------------------

    [Fact]
    public async Task RunAsync_AlpacaOpenPositionsFetchFails_AbortsRunAndWritesNothing()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedOpenPositionAsync(dbContext, "AAPL");

        var client = new FakeAlpacaTradingClient
        {
            PositionsResult = _ => Task.FromResult(new AlpacaPositionsResult
            {
                Success = false,
                ErrorMessage = "Alpaca API unavailable"
            })
        };
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(dbContext, client, pushoverNotificationService: pushover);

        var result = await service.RunAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Alpaca API unavailable", result.ErrorMessage);
        Assert.Equal(0, result.NewOrphansDetected);
        Assert.Equal(0, result.PhantomsHealed);
        Assert.Equal(0, result.PhantomsUnresolved);
        Assert.Equal(0, result.DiscrepanciesLogged);
        Assert.DoesNotContain(nameof(IAlpacaTradingClient.ListOrdersBySymbolAsync), client.CalledMethods);

        // A top-level fetch failure never reaches orphan/phantom detection at all - not one of
        // Pushover's wired trigger points (CLAUDE.md's Execution Layer: Ninth Design Decision
        // scopes this to orphan/phantom detection specifically, not every Critical log in this
        // class).
        Assert.Empty(pushover.SentMessages);

        var stillOpen = await dbContext.Positions.SingleAsync();
        Assert.Null(stillOpen.ExitDate);
    }
}
