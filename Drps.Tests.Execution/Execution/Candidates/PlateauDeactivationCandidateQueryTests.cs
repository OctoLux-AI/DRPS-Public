using Drps.Execution.Candidates;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;

namespace Drps.Tests.Execution.Candidates;

public class PlateauDeactivationCandidateQueryTests
{
    private static readonly DateTime Monday = new(2026, 7, 20, 10, 0, 0);

    private static Position BuildPosition(
        string ticker, DateTime? deactivatedDate, DateTime? exitDate = null) => new()
    {
        Ticker = ticker,
        GateScoreId = 1,
        AdjusterAllocationId = 1,
        EntryDate = Monday.AddDays(-10),
        EntryPrice = 100m,
        EntryQuantity = 10m,
        DeactivatedDate = deactivatedDate,
        ExitDate = exitDate,
        ExitPrice = exitDate is null ? null : 95m,
        ExitQuantity = exitDate is null ? null : 10m,
        ExitReason = exitDate is null ? null : PositionExitReason.PlateauDeactivation
    };

    [Fact]
    public async Task GetActionableDeactivationCandidatesAsync_DeactivatedAndOpen_IsReturned()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.Positions.Add(BuildPosition("AAPL", deactivatedDate: Monday));
        await dbContext.SaveChangesAsync();

        var query = new PlateauDeactivationCandidateQuery(dbContext);
        var result = await query.GetActionableDeactivationCandidatesAsync(CancellationToken.None);

        var position = Assert.Single(result);
        Assert.Equal("AAPL", position.Ticker);
        Assert.Null(position.ExitDate);
    }

    [Fact]
    public async Task GetActionableDeactivationCandidatesAsync_DeactivatedButAlreadyClosed_IsExcluded()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.Positions.Add(BuildPosition("AAPL", deactivatedDate: Monday.AddDays(-2), exitDate: Monday.AddDays(-1)));
        await dbContext.SaveChangesAsync();

        var query = new PlateauDeactivationCandidateQuery(dbContext);
        var result = await query.GetActionableDeactivationCandidatesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActionableDeactivationCandidatesAsync_NoDeactivatedDateAtAll_IsExcluded()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.Positions.Add(BuildPosition("AAPL", deactivatedDate: null));
        await dbContext.SaveChangesAsync();

        var query = new PlateauDeactivationCandidateQuery(dbContext);
        var result = await query.GetActionableDeactivationCandidatesAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetActionableDeactivationCandidatesAsync_NoDeactivatedDateButAlreadyClosedForOtherReason_IsExcluded()
    {
        // A position closed via a different exit path (e.g. AtrStop) with no plateau
        // deactivation stamp at all - confirms exclusion is driven by DeactivatedDate being
        // null, not merely by ExitDate being set.
        using var dbContext = InMemoryDbContextFactory.Create();
        var position = BuildPosition("AAPL", deactivatedDate: null, exitDate: Monday.AddDays(-1));
        position.ExitReason = PositionExitReason.AtrStop;
        dbContext.Positions.Add(position);
        await dbContext.SaveChangesAsync();

        var query = new PlateauDeactivationCandidateQuery(dbContext);
        var result = await query.GetActionableDeactivationCandidatesAsync(CancellationToken.None);

        Assert.Empty(result);
    }
}
