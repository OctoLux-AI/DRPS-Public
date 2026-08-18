using Drps.Execution.Candidates;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;

namespace Drps.Tests.Execution.Candidates;

public class CloseCandidateQueryTests
{
    private static readonly DateTime Monday = new(2026, 7, 20, 10, 0, 0);

    private static GateScore BuildGateScore(long id, string ticker, GateBucket bucket = GateBucket.Exit) => new()
    {
        Id = id,
        Ticker = ticker,
        ScanDate = Monday,
        Bucket = bucket,
        CompositeScore = 0.60m,
        DataAsOfDate = DateOnly.FromDateTime(Monday),
        CalculationVersion = 1,
        GateParameterVersion = 1
    };

    private static Position BuildOpenPosition(string ticker, long gateScoreId = 1, long adjusterAllocationId = 1) => new()
    {
        Ticker = ticker,
        GateScoreId = gateScoreId,
        AdjusterAllocationId = adjusterAllocationId,
        EntryDate = Monday.AddDays(-10),
        EntryPrice = 100m,
        EntryQuantity = 10m,
        ExitDate = null
    };

    [Fact]
    public async Task GetActionableCloseCandidatesAsync_ExitSignalWithMatchingOpenPosition_IsReturned()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL");
        dbContext.GateScores.Add(gateScore);
        dbContext.Positions.Add(BuildOpenPosition("AAPL"));
        await dbContext.SaveChangesAsync();

        var query = new CloseCandidateQuery(dbContext);
        var result = await query.GetActionableCloseCandidatesAsync(CancellationToken.None);

        var candidate = Assert.Single(result);
        Assert.Equal("AAPL", candidate.GateScore.Ticker);
        Assert.Equal("AAPL", candidate.Position.Ticker);
        Assert.Null(candidate.Position.ExitDate);
    }

    [Fact]
    public async Task GetActionableCloseCandidatesAsync_ExitSignalWithNoOpenPositionForTicker_IsExcluded()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL");
        dbContext.GateScores.Add(gateScore);
        // No Position at all for AAPL.
        await dbContext.SaveChangesAsync();

        var query = new CloseCandidateQuery(dbContext);
        var result = await query.GetActionableCloseCandidatesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData(GateBucket.Buy)]
    [InlineData(GateBucket.Watch)]
    [InlineData(GateBucket.Neutral)]
    public async Task GetActionableCloseCandidatesAsync_NonExitSignal_IsNeverReturned_RegardlessOfPositionState(GateBucket bucket)
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL", bucket);
        dbContext.GateScores.Add(gateScore);
        dbContext.Positions.Add(BuildOpenPosition("AAPL"));
        await dbContext.SaveChangesAsync();

        var query = new CloseCandidateQuery(dbContext);
        var result = await query.GetActionableCloseCandidatesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActionableCloseCandidatesAsync_TickerHasAlreadyClosedPositionOnly_IsExcludedNotMistakenForOpen()
    {
        // ExitDate set - a closed Position must not satisfy the "open Position exists" join,
        // same convention as LedgerPositionStateProvider.IsCurrentlyHeld (ExitDate == null only).
        using var dbContext = InMemoryDbContextFactory.Create();
        var gateScore = BuildGateScore(1, "AAPL");
        dbContext.GateScores.Add(gateScore);
        var closedPosition = BuildOpenPosition("AAPL");
        closedPosition.ExitDate = Monday.AddDays(-1);
        closedPosition.ExitPrice = 95m;
        closedPosition.ExitQuantity = 10m;
        closedPosition.ExitReason = PositionExitReason.AtrStop;
        dbContext.Positions.Add(closedPosition);
        await dbContext.SaveChangesAsync();

        var query = new CloseCandidateQuery(dbContext);
        var result = await query.GetActionableCloseCandidatesAsync(CancellationToken.None);

        Assert.Empty(result);
    }
}
