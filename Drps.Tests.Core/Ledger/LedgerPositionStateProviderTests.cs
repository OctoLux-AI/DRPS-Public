using Drps.Ingestion.Persistence;
using Drps.Ledger;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;

namespace Drps.Tests.Ledger;

public class LedgerPositionStateProviderTests
{
    private static readonly DateTime EntryDate = new(2026, 7, 10, 9, 30, 0);

    private static async Task<Position> SeedPositionAsync(
        DrpsDbContext dbContext,
        string ticker,
        DateTime? exitDate = null,
        DateTime? coolDownStartDate = null)
    {
        var position = new Position
        {
            Ticker = ticker,
            GateScoreId = 1,
            AdjusterAllocationId = 1,
            EntryDate = EntryDate,
            EntryPrice = 100m,
            EntryQuantity = 300m,
            ExitDate = exitDate,
            ExitPrice = exitDate is null ? null : 110m,
            ExitQuantity = exitDate is null ? null : 300m,
            ExitReason = exitDate is null ? null : PositionExitReason.AtrStop,
            CoolDownStartDate = coolDownStartDate
        };

        dbContext.Positions.Add(position);
        await dbContext.SaveChangesAsync();

        return position;
    }

    // Today's real shipped values (same fixture GateCompositeServiceTests/GateParametersSeeder
    // use) - only NoBuySessionCount actually matters to this provider, the rest is filled in
    // for a realistic row.
    private static async Task SeedActiveGateParametersAsync(DrpsDbContext dbContext, int noBuySessionCount = 2)
    {
        dbContext.GateParameters.Add(new GateParameters
        {
            EffectiveFrom = EntryDate.Date,
            IsActive = true,
            RsiLowerBound = 50m,
            RsiPeak = 60m,
            RsiUpperBound = 70m,
            RsiFloorQuality = 0.75m,
            RvolFloorMultiple = 1.5m,
            RvolCeilingMultiple = 3.0m,
            RvolFullWeight = 0.30m,
            RvolHalfWeight = 0.15m,
            RsiCompositeWeight = 0.70m,
            BuyThreshold = 0.85m,
            WatchThreshold = 0.75m,
            ExitThreshold = 0.70m,
            NoBuySessionCount = noBuySessionCount
        });
        await dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task IsCurrentlyHeld_OpenPosition_ReturnsTrue()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedPositionAsync(dbContext, "AAA");

        var provider = new LedgerPositionStateProvider(dbContext);

        Assert.True(provider.IsCurrentlyHeld("AAA"));
    }

    [Fact]
    public async Task IsCurrentlyHeld_ClosedPosition_ReturnsFalse()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedPositionAsync(dbContext, "AAA", exitDate: EntryDate.AddDays(2));

        var provider = new LedgerPositionStateProvider(dbContext);

        Assert.False(provider.IsCurrentlyHeld("AAA"));
    }

    [Fact]
    public void IsCurrentlyHeld_NoPositionAtAll_ReturnsFalse()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var provider = new LedgerPositionStateProvider(dbContext);

        Assert.False(provider.IsCurrentlyHeld("ZZZ"));
    }

    [Fact]
    public async Task GetNoBuyExpiration_RecentCoolDownStartDate_MatchesGateCompositeServiceRuleExactly()
    {
        // CLAUDE.md's worked example, same one GateCompositeServiceTests locks in
        // (Score_ExitOnWednesdayAfternoon_NoBuyListExpiresTheFollowingMonday): a
        // composite-degradation exit triggered mid-session on Wednesday at 2pm does not count
        // that partial session - Thursday/Friday must fully elapse, tradeable again Monday.
        var wednesdayAt2Pm = new DateTime(2026, 7, 15, 14, 0, 0); // confirmed Wednesday
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedActiveGateParametersAsync(dbContext);
        await SeedPositionAsync(dbContext, "AAA", exitDate: wednesdayAt2Pm, coolDownStartDate: wednesdayAt2Pm);

        var provider = new LedgerPositionStateProvider(dbContext);

        var expectedMonday = new DateTime(2026, 7, 20); // confirmed Monday
        Assert.Equal(expectedMonday, provider.GetNoBuyExpiration("AAA"));
    }

    [Fact]
    public async Task GetNoBuyExpiration_OldExpiredCoolDownStartDate_StillReturnsRealPastExpiration()
    {
        // Matches GateCompositeService's own behavior exactly: the returned expiration is
        // always the raw computed date, even when it has already passed - whether it's still
        // "active" is the caller's own asOf-aware comparison (GateCompositeService.Score), not
        // this provider's job. Null is reserved only for "no cooldown was ever triggered."
        var longAgo = new DateTime(2020, 1, 1, 10, 0, 0);
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedActiveGateParametersAsync(dbContext);
        await SeedPositionAsync(dbContext, "AAA", exitDate: longAgo, coolDownStartDate: longAgo);

        var provider = new LedgerPositionStateProvider(dbContext);

        var result = provider.GetNoBuyExpiration("AAA");
        Assert.NotNull(result);
        Assert.True(result < DateTime.UtcNow);
    }

    [Fact]
    public async Task GetNoBuyExpiration_NoCoolDownStartDate_ReturnsNull()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedPositionAsync(dbContext, "AAA", exitDate: EntryDate.AddDays(2));

        var provider = new LedgerPositionStateProvider(dbContext);

        Assert.Null(provider.GetNoBuyExpiration("AAA"));
    }

    [Fact]
    public async Task GetNoBuyExpiration_CoolDownStartDateButNoActiveGateParameters_Throws()
    {
        // Fail-closed guard this provider adds on top of the required behavior - without a
        // real NoBuySessionCount, guessing risks silently mis-blocking or mis-permitting a BUY.
        using var dbContext = InMemoryDbContextFactory.Create();
        await SeedPositionAsync(dbContext, "AAA", exitDate: EntryDate.AddDays(2), coolDownStartDate: EntryDate.AddDays(2));

        var provider = new LedgerPositionStateProvider(dbContext);

        Assert.Throws<InvalidOperationException>(() => provider.GetNoBuyExpiration("AAA"));
    }

    // GetShareCapDeficient tests - consolidation per CLAUDE.md's 2026-07-18 decision block,
    // point 4: Position no longer carries its own ShareCapDeficient column; this reads
    // through the AdjusterAllocationId FK to AdjusterAllocation's real, wired value instead.
    private static async Task<Position> SeedPositionWithAllocationAsync(
        DrpsDbContext dbContext, string ticker, bool shareCapDeficient)
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
            ShareCount = shareCapDeficient ? 0 : 300,
            ShareCapDeficient = shareCapDeficient,
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
            EntryQuantity = shareCapDeficient ? 0m : 300m
        };
        dbContext.Positions.Add(position);
        await dbContext.SaveChangesAsync();

        return position;
    }

    [Fact]
    public async Task GetShareCapDeficient_AllocationNotDeficient_ReturnsFalse()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var position = await SeedPositionWithAllocationAsync(dbContext, "AAA", shareCapDeficient: false);

        var provider = new LedgerPositionStateProvider(dbContext);

        Assert.False(provider.GetShareCapDeficient(position.Id));
    }

    [Fact]
    public async Task GetShareCapDeficient_AllocationIsDeficient_ReturnsTrue()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var position = await SeedPositionWithAllocationAsync(dbContext, "AAA", shareCapDeficient: true);

        var provider = new LedgerPositionStateProvider(dbContext);

        Assert.True(provider.GetShareCapDeficient(position.Id));
    }

    [Fact]
    public void GetShareCapDeficient_NonexistentPositionId_ReturnsNull()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var provider = new LedgerPositionStateProvider(dbContext);

        Assert.Null(provider.GetShareCapDeficient(999));
    }
}
