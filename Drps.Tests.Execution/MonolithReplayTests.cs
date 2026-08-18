using Drps.Monolith;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;

namespace Drps.Tests;

public class MonolithReplayTests
{
    private static GateScore MakeGateScore(long id, string ticker, DateTime scanDate, GateBucket bucket = GateBucket.Buy) =>
        new()
        {
            Id = id,
            Ticker = ticker,
            ScanDate = scanDate,
            Bucket = bucket,
            CompositeScore = 0.90m,
            CalculationVersion = 1,
            GateParameterVersion = 1
        };

    private static Position MakePosition(
        long id,
        string ticker,
        long gateScoreId,
        long adjusterAllocationId,
        DateTime entryDate,
        decimal entryPrice,
        decimal entryQuantity = 10m,
        DateTime? exitDate = null,
        decimal? exitPrice = null,
        decimal? exitQuantity = null,
        PositionExitReason? exitReason = null,
        DateTime? lowGradeDate = null,
        DateTime? plateauDate = null,
        DateTime? reactivatedDate = null,
        DateTime? deactivatedDate = null) =>
        new()
        {
            Id = id,
            Ticker = ticker,
            GateScoreId = gateScoreId,
            AdjusterAllocationId = adjusterAllocationId,
            EntryDate = entryDate,
            EntryPrice = entryPrice,
            EntryQuantity = entryQuantity,
            ExitDate = exitDate,
            ExitPrice = exitPrice,
            ExitQuantity = exitQuantity,
            ExitReason = exitReason,
            LowGradeDate = lowGradeDate,
            PlateauDate = plateauDate,
            ReactivatedDate = reactivatedDate,
            DeactivatedDate = deactivatedDate
        };

    [Fact]
    public void Join_PositionReferencesOriginatingGateScore_ResolvesViaFk()
    {
        var originatingScore = MakeGateScore(id: 10, ticker: "AAA", scanDate: new DateTime(2026, 6, 1));
        var otherScore = MakeGateScore(id: 11, ticker: "AAA", scanDate: new DateTime(2026, 6, 5), bucket: GateBucket.Neutral);
        var position = MakePosition(
            id: 100, ticker: "AAA", gateScoreId: 10, adjusterAllocationId: 1,
            entryDate: new DateTime(2026, 6, 2), entryPrice: 50m);

        var replay = ReplayJoinService.Join(position, new[] { originatingScore, otherScore });

        Assert.NotNull(replay.OriginatingScore);
        Assert.Equal(10, replay.OriginatingScore!.Id);
        Assert.Empty(replay.ReassessmentHistory);
    }

    [Fact]
    public void Join_PositionWithNoLifecycleStamps_ReturnsEmptyReassessmentHistoryWithoutThrowing()
    {
        // Current real-world case: any manually-opened Position that hasn't hit a Gate
        // re-evaluation yet has every lifecycle stamp field null.
        var originatingScore = MakeGateScore(id: 1, ticker: "NVDA", scanDate: new DateTime(2026, 7, 15));
        var position = MakePosition(
            id: 2, ticker: "NVDA", gateScoreId: 1, adjusterAllocationId: 1,
            entryDate: new DateTime(2026, 7, 16), entryPrice: 210.99m);

        var replay = ReplayJoinService.Join(position, new[] { originatingScore });

        Assert.Empty(replay.ReassessmentHistory);

        var grade = GradingService.Grade(replay);
        Assert.Equal("no reassessment history", grade.ReassessmentSummary);
    }

    [Fact]
    public void Join_LifecycleStampMatchesNearestGateScoreByTickerAndTimestamp()
    {
        var entryScore = MakeGateScore(id: 20, ticker: "BBB", scanDate: new DateTime(2026, 5, 1));
        var farScore = MakeGateScore(id: 21, ticker: "BBB", scanDate: new DateTime(2026, 5, 10), bucket: GateBucket.Watch);
        var reassessmentScore = MakeGateScore(id: 22, ticker: "BBB", scanDate: new DateTime(2026, 5, 20), bucket: GateBucket.Exit);

        var lowGradeStamp = new DateTime(2026, 5, 20, 3, 35, 0);
        var position = MakePosition(
            id: 200, ticker: "BBB", gateScoreId: 20, adjusterAllocationId: 1,
            entryDate: new DateTime(2026, 5, 2), entryPrice: 30m,
            lowGradeDate: lowGradeStamp);

        var replay = ReplayJoinService.Join(position, new[] { entryScore, farScore, reassessmentScore });

        var lowGradeEvent = Assert.Single(replay.ReassessmentHistory);
        Assert.Equal("LowGradeDate", lowGradeEvent.StampName);
        Assert.NotNull(lowGradeEvent.MatchedScan);
        Assert.Equal(22, lowGradeEvent.MatchedScan!.Id);
    }

    [Fact]
    public void Join_LifecycleStampWithNoNearbyGateScore_MatchedScanIsNullNotThrowing()
    {
        var entryScore = MakeGateScore(id: 30, ticker: "CCC", scanDate: new DateTime(2026, 4, 1));
        var position = MakePosition(
            id: 300, ticker: "CCC", gateScoreId: 30, adjusterAllocationId: 1,
            entryDate: new DateTime(2026, 4, 2), entryPrice: 15m,
            plateauDate: new DateTime(2026, 9, 1));

        var replay = ReplayJoinService.Join(position, new[] { entryScore });

        var plateauEvent = Assert.Single(replay.ReassessmentHistory);
        Assert.Equal("PlateauDate", plateauEvent.StampName);
        Assert.Null(plateauEvent.MatchedScan);

        var grade = GradingService.Grade(replay);
        Assert.Contains("no matching GateScore found", grade.ReassessmentSummary);
    }

    [Fact]
    public void Join_PositionWithUnresolvableGateScoreId_OriginatingScoreIsNullNotThrowing()
    {
        // A Position whose GateScoreId doesn't resolve against the supplied GateScore set -
        // e.g. a legacy row, or one scoped outside the caller's query window - must not crash
        // the join; it's a real, honest "can't attribute this to a known recommendation" case.
        var unrelatedScore = MakeGateScore(id: 40, ticker: "DDD", scanDate: new DateTime(2026, 3, 1));
        var position = MakePosition(
            id: 400, ticker: "DDD", gateScoreId: 999, adjusterAllocationId: 1,
            entryDate: new DateTime(2026, 3, 2), entryPrice: 20m);

        var replay = ReplayJoinService.Join(position, new[] { unrelatedScore });

        Assert.Null(replay.OriginatingScore);

        var grade = GradingService.Grade(replay);
        Assert.False(grade.WasRecommendationActedOn);
        Assert.Null(grade.TimeFromRecommendationToAction);
    }

    [Fact]
    public void SyntheticDataExclusions_FlagsExactlyTheKnownHandSeededIds()
    {
        // Matches CLAUDE.md's "Ledger Live Verification Pass - Hand-Seeded Synthetic Data
        // Flagged" block exactly: GateScore Id=2, AdjusterAllocation Id=1, Position Ids 1-2.
        Assert.True(SyntheticDataExclusions.IsSyntheticGateScore(2));
        Assert.False(SyntheticDataExclusions.IsSyntheticGateScore(1));
        Assert.False(SyntheticDataExclusions.IsSyntheticGateScore(3));

        Assert.True(SyntheticDataExclusions.IsSyntheticAdjusterAllocation(1));
        Assert.False(SyntheticDataExclusions.IsSyntheticAdjusterAllocation(2));

        Assert.True(SyntheticDataExclusions.IsSyntheticPosition(1));
        Assert.True(SyntheticDataExclusions.IsSyntheticPosition(2));
        Assert.False(SyntheticDataExclusions.IsSyntheticPosition(3));
    }

    [Fact]
    public async Task MonolithDataLoader_ExcludesSyntheticRowsFromRealQueryResults()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        dbContext.GateScores.Add(MakeGateScore(id: 2, ticker: "NVDA", scanDate: new DateTime(2026, 7, 10)));
        dbContext.GateScores.Add(MakeGateScore(id: 3, ticker: "TGT", scanDate: new DateTime(2026, 7, 15), bucket: GateBucket.Neutral));

        dbContext.Positions.Add(MakePosition(
            id: 1, ticker: "NVDA", gateScoreId: 2, adjusterAllocationId: 1,
            entryDate: new DateTime(2026, 7, 11), entryPrice: 210.99m,
            exitDate: new DateTime(2026, 7, 12), exitPrice: 215.50m, exitQuantity: 10m,
            exitReason: PositionExitReason.AtrStop));
        dbContext.Positions.Add(MakePosition(
            id: 2, ticker: "NVDA", gateScoreId: 2, adjusterAllocationId: 1,
            entryDate: new DateTime(2026, 7, 11), entryPrice: 210.99m));
        dbContext.Positions.Add(MakePosition(
            id: 3, ticker: "TGT", gateScoreId: 3, adjusterAllocationId: 2,
            entryDate: new DateTime(2026, 7, 16), entryPrice: 100m));

        await dbContext.SaveChangesAsync();

        var loader = new MonolithDataLoader(dbContext);
        var gateScores = await loader.LoadRealGateScoresAsync(CancellationToken.None);
        var positions = await loader.LoadRealPositionsAsync(CancellationToken.None);

        Assert.DoesNotContain(gateScores, g => g.Id == 2);
        Assert.Contains(gateScores, g => g.Id == 3);

        Assert.DoesNotContain(positions, p => p.Id == 1 || p.Id == 2);
        Assert.Single(positions);
        Assert.Equal(3, positions[0].Id);
    }

    [Fact]
    public void BuildReport_FullConstructedFixture_RecommendationToActionToWinExit_ProducesCorrectGrade()
    {
        // Fake GateScore -> fake Position -> fake exit, end to end, no database involved.
        var recommendation = MakeGateScore(id: 1000, ticker: "ZZZ", scanDate: new DateTime(2026, 6, 1, 3, 35, 0));

        var lowGradeStamp = new DateTime(2026, 6, 20, 3, 35, 0);
        var reassessmentScore = MakeGateScore(id: 1001, ticker: "ZZZ", scanDate: lowGradeStamp, bucket: GateBucket.Exit);

        var position = MakePosition(
            id: 5000, ticker: "ZZZ", gateScoreId: 1000, adjusterAllocationId: 1,
            entryDate: new DateTime(2026, 6, 3, 9, 30, 0), entryPrice: 40m, entryQuantity: 25m,
            exitDate: new DateTime(2026, 6, 21), exitPrice: 48m, exitQuantity: 25m,
            exitReason: PositionExitReason.CompositeDegradation,
            lowGradeDate: lowGradeStamp);

        var grades = GradingReportBuilder.BuildReport(
            new[] { recommendation, reassessmentScore }, new[] { position });

        var grade = Assert.Single(grades);
        Assert.Equal(5000, grade.PositionId);
        Assert.Equal("ZZZ", grade.Ticker);
        Assert.True(grade.WasRecommendationActedOn);
        Assert.Equal(position.EntryDate - recommendation.ScanDate, grade.TimeFromRecommendationToAction);
        Assert.Equal(PositionOutcome.Win, grade.Outcome);
        Assert.Equal(8m, grade.RealizedPnLPerShare);
        Assert.Contains("LowGradeDate", grade.ReassessmentSummary);
        Assert.Contains("GateScore 1001", grade.ReassessmentSummary);
    }

    [Theory]
    [InlineData(35, 40, PositionOutcome.Loss)]
    [InlineData(40, 40, PositionOutcome.Breakeven)]
    public void GradingService_Grade_ExitPriceRelativeToEntry_ProducesExpectedOutcome(
        decimal exitPrice, decimal entryPrice, PositionOutcome expected)
    {
        var recommendation = MakeGateScore(id: 2000, ticker: "YYY", scanDate: new DateTime(2026, 1, 1));
        var position = MakePosition(
            id: 6000, ticker: "YYY", gateScoreId: 2000, adjusterAllocationId: 1,
            entryDate: new DateTime(2026, 1, 2), entryPrice: entryPrice,
            exitDate: new DateTime(2026, 1, 10), exitPrice: exitPrice, exitQuantity: 10m,
            exitReason: PositionExitReason.AtrStop);

        var replay = ReplayJoinService.Join(position, new[] { recommendation });
        var grade = GradingService.Grade(replay);

        Assert.Equal(expected, grade.Outcome);
    }

    [Fact]
    public void GradingService_Grade_PositionStillOpen_OutcomeIsStillOpenWithNullPnL()
    {
        var recommendation = MakeGateScore(id: 3000, ticker: "XXX", scanDate: new DateTime(2026, 2, 1));
        var position = MakePosition(
            id: 7000, ticker: "XXX", gateScoreId: 3000, adjusterAllocationId: 1,
            entryDate: new DateTime(2026, 2, 2), entryPrice: 60m);

        var replay = ReplayJoinService.Join(position, new[] { recommendation });
        var grade = GradingService.Grade(replay);

        Assert.Equal(PositionOutcome.StillOpen, grade.Outcome);
        Assert.Null(grade.RealizedPnLPerShare);
    }
}
