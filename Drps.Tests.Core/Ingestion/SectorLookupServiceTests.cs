using Drps.Ingestion.Verification;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;

namespace Drps.Tests.Ingestion;

public class SectorLookupServiceTests
{
    // Both explicitly UTC-kind, sidestepping any local-timezone dependency in the test run
    // itself - SectorLookupService normalizes a non-UTC asOf internally, but RawSectorObservation.
    // FetchedAt is always written as DateTime.UtcNow in production, so exercising the service
    // with UTC on both sides here is the realistic, deterministic case.
    private static readonly DateTime AsOf = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    private static RawSectorObservation MakeObservation(
        string ticker, SectorSourceType source, string? sectorValue, string? sicCode, DateTime fetchedAt, bool verified) => new()
    {
        Ticker = ticker,
        Source = source,
        SectorValue = sectorValue,
        SicCode = sicCode,
        FetchedAt = fetchedAt,
        Verified = verified
    };

    [Fact]
    public async Task GetSectorAsync_FreshFinnhubRowWithinSevenDays_ReturnsSectorValue()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawSectorObservations.Add(MakeObservation(
            "AAPL", SectorSourceType.Finnhub, "Technology", null, AsOf.AddDays(-3), verified: true));
        await dbContext.SaveChangesAsync();

        var service = new SectorLookupService(dbContext);
        var sector = await service.GetSectorAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal("Technology", sector);
    }

    [Fact]
    public async Task GetSectorAsync_FinnhubRowOlderThanSevenDays_ReturnsNull()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawSectorObservations.Add(MakeObservation(
            "AAPL", SectorSourceType.Finnhub, "Technology", null, AsOf.AddDays(-8), verified: true));
        await dbContext.SaveChangesAsync();

        var service = new SectorLookupService(dbContext);
        var sector = await service.GetSectorAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Null(sector);
    }

    [Fact]
    public async Task GetSectorAsync_NoRowAtAll_ReturnsNull()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var service = new SectorLookupService(dbContext);
        var sector = await service.GetSectorAsync("ZZZZ", AsOf, CancellationToken.None);

        Assert.Null(sector);
    }

    [Fact]
    public async Task GetSectorAsync_OnlySecEdgarRowPresent_IsIgnoredAndReturnsNull()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Fresh, but from SecEdgar - never read by this method, even though it's the only
        // row for this ticker and well within the TTL window.
        dbContext.RawSectorObservations.Add(MakeObservation(
            "AAPL", SectorSourceType.SecEdgar, "Electronic Computers", "3571", AsOf.AddDays(-1), verified: false));
        await dbContext.SaveChangesAsync();

        var service = new SectorLookupService(dbContext);
        var sector = await service.GetSectorAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Null(sector);
    }

    [Fact]
    public async Task GetSectorAsync_MultipleFinnhubRows_ReturnsMostRecentOnly()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawSectorObservations.Add(MakeObservation(
            "AAPL", SectorSourceType.Finnhub, "Consumer Electronics", null, AsOf.AddDays(-6), verified: true));
        dbContext.RawSectorObservations.Add(MakeObservation(
            "AAPL", SectorSourceType.Finnhub, "Technology", null, AsOf.AddDays(-1), verified: true));
        await dbContext.SaveChangesAsync();

        var service = new SectorLookupService(dbContext);
        var sector = await service.GetSectorAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal("Technology", sector);
    }
}
