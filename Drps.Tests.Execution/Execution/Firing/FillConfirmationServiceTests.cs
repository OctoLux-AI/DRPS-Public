using Drps.Execution.Alpaca;
using Drps.Execution.Firing;
using Drps.Ingestion.Persistence;
using Drps.Ledger;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Execution.Firing;

public class FillConfirmationServiceTests
{
    private static readonly DateTime AsOf = new(2026, 7, 21, 10, 0, 0);
    private const string ClientOrderId = "drps-2-1-1";

    private static GateScore MakeGateScore(string ticker = "AAPL", decimal atrValue = 3.0m) => new()
    {
        Ticker = ticker,
        Bucket = GateBucket.Buy,
        CompositeScore = 0.90m,
        AtrValue = atrValue,
        ScanDate = AsOf,
        CalculationVersion = 1,
        GateParameterVersion = 1
    };

    private static AdjusterAllocation MakeAdjusterAllocation(long gateScoreId, decimal shareCount = 10m) => new()
    {
        GateScoreId = gateScoreId,
        AllocationPercent = 0.03m,
        AllocationDollarAmount = 1500m,
        ShareCount = shareCount,
        ShareCapDeficient = false,
        AsOfTimestamp = AsOf,
        AdjusterParameterVersion = 1
    };

    private static async Task<(GateScore GateScore, AdjusterAllocation Allocation)> SeedGateScoreAndAllocationAsync(
        DrpsDbContext dbContext, decimal atrValue = 3.0m, decimal shareCount = 10m)
    {
        var gateScore = MakeGateScore(atrValue: atrValue);
        dbContext.GateScores.Add(gateScore);
        await dbContext.SaveChangesAsync();

        var allocation = MakeAdjusterAllocation(gateScore.Id, shareCount);
        dbContext.AdjusterAllocations.Add(allocation);
        await dbContext.SaveChangesAsync();

        return (gateScore, allocation);
    }

    private static FillConfirmationService CreateService(
        FakeAlpacaTradingClient client,
        LedgerPositionWriter writer,
        Func<DateTime>? nowProvider = null,
        TimeSpan? pollInterval = null) =>
        new(
            client,
            writer,
            NullLogger<FillConfirmationService>.Instance,
            delay: (_, _) => Task.CompletedTask,
            nowProvider: nowProvider ?? (() => AsOf),
            pollInterval: pollInterval ?? TimeSpan.FromSeconds(5));

    // --- Open: successful fill --------------------------------------------------------------

    [Fact]
    public async Task ConfirmOpenAsync_FilledOrder_WritesEntryFieldsAndSeedsAtrAndTpTargetPrice()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScore, allocation) = await SeedGateScoreAndAllocationAsync(dbContext, atrValue: 3.0m);

        var client = new FakeAlpacaTradingClient
        {
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-1", Status = "filled", FilledQuantity = 10m, FilledAveragePrice = 150.25m
            })
        };
        var writer = new LedgerPositionWriter(dbContext);
        var service = CreateService(client, writer);

        var result = await service.ConfirmOpenAsync(
            ClientOrderId, gateScore, allocation, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(FillConfirmationOutcome.Recorded, result.Outcome);
        Assert.NotNull(result.Position);
        Assert.Equal(gateScore.Ticker, result.Position!.Ticker);
        Assert.Equal(gateScore.Id, result.Position.GateScoreId);
        Assert.Equal(allocation.Id, result.Position.AdjusterAllocationId);
        Assert.Equal(AsOf, result.Position.EntryDate);
        Assert.Equal(150.25m, result.Position.EntryPrice);
        Assert.Equal(10m, result.Position.EntryQuantity);
        Assert.Equal(3.0m, result.Position.EntryAtr);
        // N=2: EntryPrice + 2 x EntryAtr = 150.25 + 6.00 = 156.25.
        Assert.Equal(156.25m, result.Position.TpTargetPrice);

        var persisted = await dbContext.Positions.SingleAsync();
        Assert.Equal(3.0m, persisted.EntryAtr);
        Assert.Equal(156.25m, persisted.TpTargetPrice);
    }

    // --- Open: partial fill that then settles into canceled/expired ------------------------

    [Fact]
    public async Task ConfirmOpenAsync_PartiallyFilledThenCanceled_StillRecordsThePartialFillAsIs()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScore, allocation) = await SeedGateScoreAndAllocationAsync(dbContext, atrValue: 2.0m);

        var client = new FakeAlpacaTradingClient
        {
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-1", Status = "canceled", FilledQuantity = 4m, FilledAveragePrice = 150m
            })
        };
        var writer = new LedgerPositionWriter(dbContext);
        var service = CreateService(client, writer);

        var result = await service.ConfirmOpenAsync(
            ClientOrderId, gateScore, allocation, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(FillConfirmationOutcome.Recorded, result.Outcome);
        Assert.Equal(4m, result.Position!.EntryQuantity);
        Assert.Equal(150m, result.Position.EntryPrice);
    }

    // --- Open/Close: terminal states with nothing filled write nothing ---------------------

    [Theory]
    [InlineData("canceled")]
    [InlineData("expired")]
    [InlineData("rejected")]
    public async Task ConfirmOpenAsync_TerminalWithZeroFill_WritesNothingToLedger(string status)
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScore, allocation) = await SeedGateScoreAndAllocationAsync(dbContext);

        var client = new FakeAlpacaTradingClient
        {
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-1", Status = status, FilledQuantity = 0m, FilledAveragePrice = null
            })
        };
        var writer = new LedgerPositionWriter(dbContext);
        var service = CreateService(client, writer);

        var result = await service.ConfirmOpenAsync(
            ClientOrderId, gateScore, allocation, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(FillConfirmationOutcome.NoFillRecorded, result.Outcome);
        Assert.Null(result.Position);
        Assert.Empty(await dbContext.Positions.ToListAsync());
    }

    [Fact]
    public async Task ConfirmOpenAsync_Rejected_NeverRecordsEvenIfFilledQuantityIsNonsensicallyPositive()
    {
        // Defensive: a rejected order can never carry a real fill. Confirms the status check
        // itself is authoritative, not merely a FilledQuantity > 0 check.
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScore, allocation) = await SeedGateScoreAndAllocationAsync(dbContext);

        var client = new FakeAlpacaTradingClient
        {
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-1", Status = "rejected", FilledQuantity = 10m, FilledAveragePrice = 150m
            })
        };
        var writer = new LedgerPositionWriter(dbContext);
        var service = CreateService(client, writer);

        var result = await service.ConfirmOpenAsync(
            ClientOrderId, gateScore, allocation, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(FillConfirmationOutcome.NoFillRecorded, result.Outcome);
        Assert.Empty(await dbContext.Positions.ToListAsync());
    }

    // --- Non-terminal past maxWait -----------------------------------------------------------

    [Fact]
    public async Task ConfirmOpenAsync_NeverReachesTerminalWithinMaxWait_StopsPollingAndWritesNothing()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScore, allocation) = await SeedGateScoreAndAllocationAsync(dbContext);

        var client = new FakeAlpacaTradingClient
        {
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-1", Status = "accepted"
            })
        };
        var writer = new LedgerPositionWriter(dbContext);
        // pollInterval=5s, maxWait=10s -> exactly 3 polls (t=0, t=5, t=10) before timing out,
        // a bounded loop rather than an unintentionally infinite one.
        var service = CreateService(client, writer, pollInterval: TimeSpan.FromSeconds(5));

        var result = await service.ConfirmOpenAsync(
            ClientOrderId, gateScore, allocation, TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal(FillConfirmationOutcome.TimedOut, result.Outcome);
        Assert.Null(result.Position);
        Assert.Equal(3, client.CalledMethods.Count(m => m == "GetOrderByClientOrderIdAsync"));
        Assert.Empty(await dbContext.Positions.ToListAsync());
    }

    // --- Anomalous fill data (defensive) ------------------------------------------------------

    [Fact]
    public async Task ConfirmOpenAsync_FilledStatusButMissingFillData_LogsCriticalAndWritesNothing()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScore, allocation) = await SeedGateScoreAndAllocationAsync(dbContext);

        var client = new FakeAlpacaTradingClient
        {
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-1", Status = "filled", FilledQuantity = null, FilledAveragePrice = null
            })
        };
        var writer = new LedgerPositionWriter(dbContext);
        var service = CreateService(client, writer);

        var result = await service.ConfirmOpenAsync(
            ClientOrderId, gateScore, allocation, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(FillConfirmationOutcome.AnomalousFillData, result.Outcome);
        Assert.Empty(await dbContext.Positions.ToListAsync());
    }

    // --- Open: LedgerPositionWriter anomaly (duplicate-open) ----------------------------------

    [Fact]
    public async Task ConfirmOpenAsync_DuplicateOpenPositionException_LogsLedgerWriteAnomalyWithoutThrowingOrRecording()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScore, allocation) = await SeedGateScoreAndAllocationAsync(dbContext);

        var client = new FakeAlpacaTradingClient
        {
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-1", Status = "filled", FilledQuantity = 10m, FilledAveragePrice = 150m
            })
        };

        var simulatedConstraintViolation = new DbUpdateException(
            "Cannot insert duplicate key row in object 'dbo.Positions' with unique index 'IX_Positions_Ticker'.",
            new Exception("unique constraint violation"));
        var writer = new LedgerPositionWriter(dbContext, saveChangesAsync: _ => throw simulatedConstraintViolation);
        var service = CreateService(client, writer);

        // Does not throw - the anomaly is caught, logged, and surfaced as a distinct outcome.
        var result = await service.ConfirmOpenAsync(
            ClientOrderId, gateScore, allocation, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(FillConfirmationOutcome.LedgerWriteAnomaly, result.Outcome);
        Assert.Contains("already has an open position", result.Reason);
        Assert.Empty(await dbContext.Positions.ToListAsync());
    }

    // --- Close: successful fill ---------------------------------------------------------------

    [Fact]
    public async Task ConfirmCloseAsync_FilledOrder_WritesExitFields()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScore, allocation) = await SeedGateScoreAndAllocationAsync(dbContext);
        var writer = new LedgerPositionWriter(dbContext);
        var opened = await writer.OpenPositionAsync(
            gateScore.Id, allocation.Id, gateScore.Ticker, AsOf, 150m, 10m, CancellationToken.None, PositionActionOrigin.Automated);

        var exitTime = AsOf.AddDays(2);
        var client = new FakeAlpacaTradingClient
        {
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-close-1", Status = "filled", FilledQuantity = 10m, FilledAveragePrice = 162.50m
            })
        };
        var service = CreateService(client, writer, nowProvider: () => exitTime);

        var result = await service.ConfirmCloseAsync(
            "drps-close-1-1", opened, PositionExitReason.AtrStop, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(FillConfirmationOutcome.Recorded, result.Outcome);

        var closed = await dbContext.Positions.SingleAsync(p => p.Id == opened.Id);
        Assert.Equal(exitTime, closed.ExitDate);
        Assert.Equal(162.50m, closed.ExitPrice);
        Assert.Equal(10m, closed.ExitQuantity);
        Assert.Equal(PositionExitReason.AtrStop, closed.ExitReason);
    }

    [Fact]
    public async Task ConfirmCloseAsync_TerminalWithZeroFill_WritesNothingAndPositionStaysOpen()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScore, allocation) = await SeedGateScoreAndAllocationAsync(dbContext);
        var writer = new LedgerPositionWriter(dbContext);
        var opened = await writer.OpenPositionAsync(
            gateScore.Id, allocation.Id, gateScore.Ticker, AsOf, 150m, 10m, CancellationToken.None, PositionActionOrigin.Automated);

        var client = new FakeAlpacaTradingClient
        {
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-close-1", Status = "rejected", FilledQuantity = 0m, FilledAveragePrice = null
            })
        };
        var service = CreateService(client, writer);

        var result = await service.ConfirmCloseAsync(
            "drps-close-1-1", opened, PositionExitReason.AtrStop, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(FillConfirmationOutcome.NoFillRecorded, result.Outcome);
        var stillOpen = await dbContext.Positions.SingleAsync(p => p.Id == opened.Id);
        Assert.Null(stillOpen.ExitDate);
    }

    // --- Close: LedgerPositionWriter anomaly (concurrent modification) ------------------------

    [Fact]
    public async Task ConfirmCloseAsync_ConcurrentModification_LogsLedgerWriteAnomalyWithoutThrowing()
    {
        // Same idiom as ManualPositionEntryServiceTests' concurrency test: EF Core's InMemory
        // provider never regenerates RowVersion on its own, so the tracked entity's OriginalValue
        // is forced directly to simulate "another writer already changed this row."
        using var dbContext = InMemoryDbContextFactory.Create();
        var (gateScore, allocation) = await SeedGateScoreAndAllocationAsync(dbContext);
        var writer = new LedgerPositionWriter(dbContext);
        var opened = await writer.OpenPositionAsync(
            gateScore.Id, allocation.Id, gateScore.Ticker, AsOf, 150m, 10m, CancellationToken.None, PositionActionOrigin.Automated);

        var tracked = await dbContext.Positions.FirstAsync(p => p.Id == opened.Id);
        dbContext.Entry(tracked).Property(p => p.RowVersion).OriginalValue = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        var client = new FakeAlpacaTradingClient
        {
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-close-1", Status = "filled", FilledQuantity = 10m, FilledAveragePrice = 160m
            })
        };
        var service = CreateService(client, writer);

        var result = await service.ConfirmCloseAsync(
            "drps-close-1-1", opened, PositionExitReason.AtrStop, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(FillConfirmationOutcome.LedgerWriteAnomaly, result.Outcome);
        Assert.Contains("modified concurrently", result.Reason);

        var stillOpen = await dbContext.Positions.AsNoTracking().SingleAsync(p => p.Id == opened.Id);
        Assert.Null(stillOpen.ExitDate);
    }

    // --- Close: consecutive-loss circuit breaker via the automated path -----------------------
    //
    // The two close tests above use win-producing prices (150->162.50, 150->160) and never
    // assert on ConsecutiveLossCircuitBreaker - per the 2026-07-26 audit, this left the breaker's
    // behavior via the automated path (FillConfirmationService -> LedgerPositionWriter.
    // ClosePositionAsync) completely unverified despite UpdateCircuitBreakerAsync running on
    // every close regardless of caller (CLAUDE.md's Execution Layer: Consecutive-Loss Circuit
    // Breaker Design Decision, point 5). These tests close that gap - same assertion style as the
    // 15 breaker tests in ManualPositionEntryServiceTests.cs, invoked through ConfirmCloseAsync
    // instead of the manual service.

    private async Task<Position> OpenPositionForBreakerTestAsync(
        DrpsDbContext dbContext, LedgerPositionWriter writer, string ticker, decimal entryPrice)
    {
        var gateScore = MakeGateScore(ticker: ticker);
        dbContext.GateScores.Add(gateScore);
        await dbContext.SaveChangesAsync();

        var allocation = MakeAdjusterAllocation(gateScore.Id);
        dbContext.AdjusterAllocations.Add(allocation);
        await dbContext.SaveChangesAsync();

        return await writer.OpenPositionAsync(
            gateScore.Id, allocation.Id, ticker, AsOf, entryPrice, 10m, CancellationToken.None, PositionActionOrigin.Automated);
    }

    private FillConfirmationService CreateFilledCloseService(LedgerPositionWriter writer, decimal filledPrice, decimal filledQuantity = 10m)
    {
        var client = new FakeAlpacaTradingClient
        {
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-close", Status = "filled", FilledQuantity = filledQuantity, FilledAveragePrice = filledPrice
            })
        };
        return CreateService(client, writer);
    }

    [Fact]
    public async Task ConfirmCloseAsync_GenuineLoss_IncrementsCircuitBreakerCount()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var writer = new LedgerPositionWriter(dbContext);
        var opened = await OpenPositionForBreakerTestAsync(dbContext, writer, "AAA", entryPrice: 150m);

        var service = CreateFilledCloseService(writer, filledPrice: 140m);

        await service.ConfirmCloseAsync(
            "drps-close-loss-1", opened, PositionExitReason.AtrStop, TimeSpan.FromSeconds(30), CancellationToken.None);

        var breaker = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        Assert.Equal(1, breaker.ConsecutiveLossCount);
        Assert.False(breaker.Tripped);
        Assert.Null(breaker.TrippedAt);
    }

    [Fact]
    public async Task ConfirmCloseAsync_ThreeConsecutiveAutomatedLosses_TripsCircuitBreaker()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var writer = new LedgerPositionWriter(dbContext);

        var openedA = await OpenPositionForBreakerTestAsync(dbContext, writer, "AAA", entryPrice: 150m);
        await CreateFilledCloseService(writer, filledPrice: 140m).ConfirmCloseAsync(
            "drps-close-loss-1", openedA, PositionExitReason.AtrStop, TimeSpan.FromSeconds(30), CancellationToken.None);

        var openedB = await OpenPositionForBreakerTestAsync(dbContext, writer, "BBB", entryPrice: 50m);
        await CreateFilledCloseService(writer, filledPrice: 40m).ConfirmCloseAsync(
            "drps-close-loss-2", openedB, PositionExitReason.CompositeDegradation, TimeSpan.FromSeconds(30), CancellationToken.None);

        var openedC = await OpenPositionForBreakerTestAsync(dbContext, writer, "CCC", entryPrice: 25m);
        await CreateFilledCloseService(writer, filledPrice: 20m).ConfirmCloseAsync(
            "drps-close-loss-3", openedC, PositionExitReason.SectorDisplacement, TimeSpan.FromSeconds(30), CancellationToken.None);

        var breaker = await dbContext.ConsecutiveLossCircuitBreakers.SingleAsync();
        Assert.Equal(3, breaker.ConsecutiveLossCount);
        Assert.True(breaker.Tripped);
        Assert.NotNull(breaker.TrippedAt);
    }
}
