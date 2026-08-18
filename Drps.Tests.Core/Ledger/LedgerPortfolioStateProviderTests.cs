using Drps.Ingestion.Persistence;
using Drps.Ledger;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;

namespace Drps.Tests.Ledger;

public class LedgerPortfolioStateProviderTests
{
    private static readonly DateTime EntryDate = new(2026, 7, 10, 9, 30, 0);

    private static async Task<GateScore> SeedGateScoreAsync(
        DrpsDbContext dbContext, string ticker, string? sector, decimal compositeScore = 0.89m)
    {
        var gateScore = new GateScore
        {
            Ticker = ticker,
            Sector = sector,
            Bucket = GateBucket.Buy,
            CompositeScore = compositeScore,
            ScanDate = EntryDate,
            CalculationVersion = 1,
            GateParameterVersion = 1
        };

        dbContext.GateScores.Add(gateScore);
        await dbContext.SaveChangesAsync();

        return gateScore;
    }

    private static async Task<Position> SeedPositionAsync(
        DrpsDbContext dbContext,
        long gateScoreId,
        string ticker,
        decimal entryPrice,
        decimal entryQuantity,
        DateTime? exitDate = null)
    {
        var position = new Position
        {
            Ticker = ticker,
            GateScoreId = gateScoreId,
            AdjusterAllocationId = 1,
            EntryDate = EntryDate,
            EntryPrice = entryPrice,
            EntryQuantity = entryQuantity,
            ExitDate = exitDate,
            ExitPrice = exitDate is null ? null : entryPrice,
            ExitQuantity = exitDate is null ? null : entryQuantity,
            ExitReason = exitDate is null ? null : PositionExitReason.AtrStop
        };

        dbContext.Positions.Add(position);
        await dbContext.SaveChangesAsync();

        return position;
    }

    [Fact]
    public async Task GetCurrentStateAsync_MultipleOpenPositions_SumsCorrectTotal()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var aaa = await SeedGateScoreAsync(dbContext, "AAA", "Technology");
        var bbb = await SeedGateScoreAsync(dbContext, "BBB", "Healthcare");
        await SeedPositionAsync(dbContext, aaa.Id, "AAA", 100m, 300m); // 30,000
        await SeedPositionAsync(dbContext, bbb.Id, "BBB", 50m, 200m);  // 10,000

        var provider = new LedgerPortfolioStateProvider(dbContext);
        var state = await provider.GetCurrentStateAsync(CancellationToken.None);

        Assert.Equal(40000m, state.TotalDeployedCapital);
    }

    [Fact]
    public async Task GetCurrentStateAsync_MultiplePositionsAcrossSectors_GroupsCorrectlyPerSector()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var aaa = await SeedGateScoreAsync(dbContext, "AAA", "Technology");
        var bbb = await SeedGateScoreAsync(dbContext, "BBB", "Technology");
        var ccc = await SeedGateScoreAsync(dbContext, "CCC", "Healthcare");
        await SeedPositionAsync(dbContext, aaa.Id, "AAA", 100m, 300m); // Technology: 30,000
        await SeedPositionAsync(dbContext, bbb.Id, "BBB", 50m, 200m);  // Technology: 10,000
        await SeedPositionAsync(dbContext, ccc.Id, "CCC", 20m, 500m);  // Healthcare: 10,000

        var provider = new LedgerPortfolioStateProvider(dbContext);
        var state = await provider.GetCurrentStateAsync(CancellationToken.None);

        Assert.Equal(2, state.DeployedCapitalBySector.Count);
        Assert.Equal(40000m, state.DeployedCapitalBySector["Technology"]);
        Assert.Equal(10000m, state.DeployedCapitalBySector["Healthcare"]);
    }

    [Fact]
    public async Task GetCurrentStateAsync_ClosedPosition_ExcludedFromTotalAndSector()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var aaa = await SeedGateScoreAsync(dbContext, "AAA", "Technology");
        await SeedPositionAsync(dbContext, aaa.Id, "AAA", 100m, 300m, exitDate: EntryDate.AddDays(2));

        var provider = new LedgerPortfolioStateProvider(dbContext);
        var state = await provider.GetCurrentStateAsync(CancellationToken.None);

        Assert.Equal(0m, state.TotalDeployedCapital);
        Assert.Empty(state.DeployedCapitalBySector);
    }

    [Fact]
    public async Task GetCurrentStateAsync_NullSectorPosition_ContributesToTotalButNotSectorBreakdown()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var aaa = await SeedGateScoreAsync(dbContext, "AAA", sector: null);
        var bbb = await SeedGateScoreAsync(dbContext, "BBB", "Technology");
        await SeedPositionAsync(dbContext, aaa.Id, "AAA", 100m, 300m); // 30,000, no sector
        await SeedPositionAsync(dbContext, bbb.Id, "BBB", 50m, 200m);  // 10,000, Technology

        var provider = new LedgerPortfolioStateProvider(dbContext);
        var state = await provider.GetCurrentStateAsync(CancellationToken.None);

        Assert.Equal(40000m, state.TotalDeployedCapital);
        Assert.Single(state.DeployedCapitalBySector);
        Assert.Equal(10000m, state.DeployedCapitalBySector["Technology"]);
    }

    [Fact]
    public async Task GetCurrentStateAsync_ZeroOpenPositions_MatchesStubPortfolioStateProviderShape()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var provider = new LedgerPortfolioStateProvider(dbContext);
        var state = await provider.GetCurrentStateAsync(CancellationToken.None);

        Assert.Equal(0m, state.TotalDeployedCapital);
        Assert.Empty(state.DeployedCapitalBySector);
        Assert.Equal(0, state.OpenPositionCount);
        Assert.Null(state.WeakestHeldPosition);
    }

    // CLAUDE.md's "Adjuster: Concurrent-Position-Cap Displacement, 10% Relative Composite-
    // Score Margin" (2026-08-01) - OpenPositionCount/WeakestHeldPosition tests below.

    [Fact]
    public async Task GetCurrentStateAsync_MultipleOpenPositions_ReportsCorrectOpenPositionCount()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var aaa = await SeedGateScoreAsync(dbContext, "AAA", "Technology");
        var bbb = await SeedGateScoreAsync(dbContext, "BBB", "Healthcare");
        var ccc = await SeedGateScoreAsync(dbContext, "CCC", "Healthcare");
        await SeedPositionAsync(dbContext, aaa.Id, "AAA", 100m, 300m);
        await SeedPositionAsync(dbContext, bbb.Id, "BBB", 50m, 200m);
        await SeedPositionAsync(dbContext, ccc.Id, "CCC", 20m, 500m, exitDate: EntryDate.AddDays(2)); // closed

        var provider = new LedgerPortfolioStateProvider(dbContext);
        var state = await provider.GetCurrentStateAsync(CancellationToken.None);

        // Only the 2 still-open positions count - matches PreFireGateService's own
        // ConcurrentPositionCap definition of "open" (ExitDate == null) exactly.
        Assert.Equal(2, state.OpenPositionCount);
    }

    [Fact]
    public async Task GetCurrentStateAsync_MultipleOpenPositionsAcrossSectors_WeakestIsAccountWideNotSectorScoped()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Deliberately cross-sector: the account-wide weakest (Healthcare/BBB at 0.86) is a
        // different sector than the strongest (Technology/AAA at 0.95) - proves the comparison
        // ignores sector entirely, per the locked design decision's "NOT scoped by sector"
        // requirement.
        var aaa = await SeedGateScoreAsync(dbContext, "AAA", "Technology", compositeScore: 0.95m);
        var bbb = await SeedGateScoreAsync(dbContext, "BBB", "Healthcare", compositeScore: 0.86m);
        var ccc = await SeedGateScoreAsync(dbContext, "CCC", "Technology", compositeScore: 0.90m);
        await SeedPositionAsync(dbContext, aaa.Id, "AAA", 100m, 300m);
        await SeedPositionAsync(dbContext, bbb.Id, "BBB", 50m, 200m);
        await SeedPositionAsync(dbContext, ccc.Id, "CCC", 20m, 500m);

        var provider = new LedgerPortfolioStateProvider(dbContext);
        var state = await provider.GetCurrentStateAsync(CancellationToken.None);

        Assert.NotNull(state.WeakestHeldPosition);
        Assert.Equal("BBB", state.WeakestHeldPosition.Ticker);
        Assert.Equal(0.86m, state.WeakestHeldPosition.CompositeScore);
    }

    [Fact]
    public async Task GetCurrentStateAsync_ClosedPosition_ExcludedFromWeakestComparison()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var aaa = await SeedGateScoreAsync(dbContext, "AAA", "Technology", compositeScore: 0.95m);
        var bbb = await SeedGateScoreAsync(dbContext, "BBB", "Healthcare", compositeScore: 0.70m);
        await SeedPositionAsync(dbContext, aaa.Id, "AAA", 100m, 300m);
        // BBB is genuinely the weakest by score, but closed - must not surface as the
        // account's weakest HELD position.
        await SeedPositionAsync(dbContext, bbb.Id, "BBB", 50m, 200m, exitDate: EntryDate.AddDays(2));

        var provider = new LedgerPortfolioStateProvider(dbContext);
        var state = await provider.GetCurrentStateAsync(CancellationToken.None);

        Assert.NotNull(state.WeakestHeldPosition);
        Assert.Equal("AAA", state.WeakestHeldPosition.Ticker);
    }

    [Fact]
    public async Task GetCurrentStateAsync_PositionWithUnresolvedGateScoreLink_ExcludedFromWeakestComparison()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var aaa = await SeedGateScoreAsync(dbContext, "AAA", "Technology", compositeScore: 0.95m);
        await SeedPositionAsync(dbContext, aaa.Id, "AAA", 100m, 300m);
        // GateScoreId 999999 does not exist - same "unresolved link" case already covered for
        // the sector breakdown, now also proven for the weakest-held comparison.
        await SeedPositionAsync(dbContext, 999999, "ORPHAN", 10m, 100m);

        var provider = new LedgerPortfolioStateProvider(dbContext);
        var state = await provider.GetCurrentStateAsync(CancellationToken.None);

        // OpenPositionCount still counts both (matches PreFireGateService's own unconditional
        // ExitDate == null count) - only the weakest-held COMPARISON excludes the orphan.
        Assert.Equal(2, state.OpenPositionCount);
        Assert.NotNull(state.WeakestHeldPosition);
        Assert.Equal("AAA", state.WeakestHeldPosition.Ticker);
    }
}
