using Drps.Execution.Candidates;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Execution.Candidates;

public class CandidateOrchestratorTests
{
    private static readonly DateTime Monday = new(2026, 7, 20, 10, 0, 0);

    private static GateScore BuildGateScore(long id, string ticker, GateBucket bucket) => new()
    {
        Id = id,
        Ticker = ticker,
        ScanDate = Monday,
        Bucket = bucket,
        CompositeScore = bucket == GateBucket.Exit ? 0.60m : 0.90m,
        DataAsOfDate = DateOnly.FromDateTime(Monday),
        CalculationVersion = 1,
        GateParameterVersion = 1
    };

    private static AdjusterAllocation BuildAllocation(long id, long gateScoreId) => new()
    {
        Id = id,
        GateScoreId = gateScoreId,
        AllocationDollarAmount = 1000m,
        AllocationPercent = 0.03m,
        ShareCount = 10m,
        ShareCapDeficient = false,
        AsOfTimestamp = Monday,
        AdjusterParameterVersion = 1,
        InsiderMultiplierApplied = 1.0m,
        InsiderDataUnverified = false
    };

    private static Position BuildOpenPosition(
        long id, string ticker, DateTime? deactivatedDate = null, long gateScoreId = 1, long adjusterAllocationId = 1) => new()
    {
        Id = id,
        Ticker = ticker,
        GateScoreId = gateScoreId,
        AdjusterAllocationId = adjusterAllocationId,
        EntryDate = Monday.AddDays(-10),
        EntryPrice = 100m,
        EntryQuantity = 10m,
        ExitDate = null,
        DeactivatedDate = deactivatedDate
    };

    private static CandidateOrchestrator CreateOrchestrator(DrpsDbContext dbContext) => new(
        // Clock fixed to the same Monday moment every GateScore.ScanDate in this file uses, so
        // OpenCandidateQuery's staleness gate (CLAUDE.md's Execution Layer: Seventh Design
        // Decision) never affects these orchestration-composition tests - 0 elapsed trading
        // days is always fresh. Staleness itself is covered by OpenCandidateQueryTests.
        new OpenCandidateQuery(dbContext, NullLogger<OpenCandidateQuery>.Instance, () => Monday),
        new CloseCandidateQuery(dbContext),
        new PlateauDeactivationCandidateQuery(dbContext));

    [Fact]
    public async Task GetActionableCandidatesAsync_PositionSatisfiesBothCloseAndPlateauQueries_ProducesExactlyOneCloseAction()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var exitGateScore = BuildGateScore(1, "AAPL", GateBucket.Exit);
        dbContext.GateScores.Add(exitGateScore);
        // DeactivatedDate set AND an Exit-bucket GateScore exists for the same ticker/open
        // position - genuinely satisfies CloseCandidateQuery and PlateauDeactivationCandidateQuery
        // at once, the exact overlap CLAUDE.md left for this orchestrator to resolve.
        dbContext.Positions.Add(BuildOpenPosition(1, "AAPL", deactivatedDate: Monday.AddDays(-1)));
        await dbContext.SaveChangesAsync();

        var orchestrator = CreateOrchestrator(dbContext);
        var result = await orchestrator.GetActionableCandidatesAsync(CancellationToken.None);

        // The overlap case (CLAUDE.md's Execution Layer: Candidate Orchestrator Correction) -
        // still exactly one CloseAction, but BOTH triggering reasons must be preserved, not
        // collapsed to whichever source happened to be processed first.
        var closeAction = Assert.Single(result.CloseActions);
        Assert.Equal(1, closeAction.Position.Id);
        Assert.Equal("AAPL", closeAction.Position.Ticker);
        Assert.True(closeAction.FromCompositeDegradation);
        Assert.True(closeAction.FromPlateauDeactivation);
        Assert.Empty(result.OpenActions);
    }

    [Fact]
    public async Task GetActionableCandidatesAsync_PositionInOnlyCloseCandidateQuery_ProducesExactlyOneCloseAction()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var exitGateScore = BuildGateScore(1, "AAPL", GateBucket.Exit);
        dbContext.GateScores.Add(exitGateScore);
        // No DeactivatedDate - only the composite-degradation exit signal applies.
        dbContext.Positions.Add(BuildOpenPosition(1, "AAPL", deactivatedDate: null));
        await dbContext.SaveChangesAsync();

        var orchestrator = CreateOrchestrator(dbContext);
        var result = await orchestrator.GetActionableCandidatesAsync(CancellationToken.None);

        var closeAction = Assert.Single(result.CloseActions);
        Assert.Equal("AAPL", closeAction.Position.Ticker);
        Assert.True(closeAction.FromCompositeDegradation);
        Assert.False(closeAction.FromPlateauDeactivation);
    }

    [Fact]
    public async Task GetActionableCandidatesAsync_PositionInOnlyPlateauDeactivationQuery_ProducesExactlyOneCloseAction()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // No GateScore row at all for this ticker - only the plateau-deactivation stamp
        // applies, confirming the dedup logic doesn't require both sources to be present.
        dbContext.Positions.Add(BuildOpenPosition(1, "MSFT", deactivatedDate: Monday.AddDays(-1)));
        await dbContext.SaveChangesAsync();

        var orchestrator = CreateOrchestrator(dbContext);
        var result = await orchestrator.GetActionableCandidatesAsync(CancellationToken.None);

        var closeAction = Assert.Single(result.CloseActions);
        Assert.Equal("MSFT", closeAction.Position.Ticker);
        Assert.False(closeAction.FromCompositeDegradation);
        Assert.True(closeAction.FromPlateauDeactivation);
    }

    [Fact]
    public async Task GetActionableCandidatesAsync_OpenAndCloseCandidatesBothExist_NeverMixIntoSameList()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        // Open candidate: BUY-bucket GateScore + AdjusterAllocation, no Position at all for
        // this ticker.
        var buyGateScore = BuildGateScore(1, "NVDA", GateBucket.Buy);
        dbContext.GateScores.Add(buyGateScore);
        dbContext.AdjusterAllocations.Add(BuildAllocation(1, buyGateScore.Id));

        // Close candidate: a different ticker, exit-signal GateScore + open Position.
        var exitGateScore = BuildGateScore(2, "AAPL", GateBucket.Exit);
        dbContext.GateScores.Add(exitGateScore);
        dbContext.Positions.Add(BuildOpenPosition(1, "AAPL"));

        await dbContext.SaveChangesAsync();

        var orchestrator = CreateOrchestrator(dbContext);
        var result = await orchestrator.GetActionableCandidatesAsync(CancellationToken.None);

        var openAction = Assert.Single(result.OpenActions);
        Assert.Equal("NVDA", openAction.GateScore.Ticker);

        var closeAction = Assert.Single(result.CloseActions);
        Assert.Equal("AAPL", closeAction.Position.Ticker);

        // Explicit cross-contamination check, not just independent counts: neither ticker
        // leaks into the other list.
        Assert.DoesNotContain(result.OpenActions, o => o.GateScore.Ticker == "AAPL");
        Assert.DoesNotContain(result.CloseActions, c => c.Position.Ticker == "NVDA");
    }

    [Fact]
    public async Task GetActionableCandidatesAsync_AllThreeQueriesEmpty_ReturnsEmptyResultWithoutThrowing()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Deliberately no data seeded at all.

        var orchestrator = CreateOrchestrator(dbContext);
        var result = await orchestrator.GetActionableCandidatesAsync(CancellationToken.None);

        Assert.Empty(result.OpenActions);
        Assert.Empty(result.CloseActions);
    }
}
