using Drps.Ingestion.Verification;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;

namespace Drps.Tests.Ingestion;

public class EarningsLookupServiceTests
{
    // Explicitly UTC-kind, sidestepping any local-timezone dependency in the test run itself -
    // same precedent as SectorLookupServiceTests' identical AsOf field. RawEarningsObservation.
    // FetchedAt is always written as DateTime.UtcNow in production, so exercising the service
    // with UTC on both sides here is the realistic, deterministic case.
    private static readonly DateTime AsOf = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    private static RawEarningsObservation MakeObservation(
        string ticker, DateOnly? nextEarningsDate, DateTime fetchedAt, EarningsFetchOutcome fetchOutcome) => new()
    {
        Ticker = ticker,
        Source = SourceType.Finnhub,
        NextEarningsDate = nextEarningsDate,
        FetchedAt = fetchedAt,
        FetchOutcome = fetchOutcome
    };

    [Fact]
    public async Task GetBlackoutStatusAsync_FreshVerifiedRowWithDateFarInFuture_ReturnsClear()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawEarningsObservations.Add(MakeObservation(
            "AAPL", DateOnly.FromDateTime(AsOf.AddDays(10)), AsOf.AddDays(-1), fetchOutcome: EarningsFetchOutcome.UpcomingEarningsFound));
        await dbContext.SaveChangesAsync();

        var service = new EarningsLookupService(dbContext);
        var status = await service.GetBlackoutStatusAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(EarningsBlackoutStatus.Clear, status);
    }

    [Fact]
    public async Task GetBlackoutStatusAsync_FreshVerifiedRowWithDateWithin48Hours_ReturnsBlackoutActive()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawEarningsObservations.Add(MakeObservation(
            "AAPL", DateOnly.FromDateTime(AsOf.AddHours(24)), AsOf.AddDays(-1), fetchOutcome: EarningsFetchOutcome.UpcomingEarningsFound));
        await dbContext.SaveChangesAsync();

        var service = new EarningsLookupService(dbContext);
        var status = await service.GetBlackoutStatusAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(EarningsBlackoutStatus.BlackoutActive, status);
    }

    [Fact]
    public async Task GetBlackoutStatusAsync_DateExactlyAtFortyEightHourBoundary_ReturnsBlackoutActive()
    {
        // "<=48h" is inclusive at the boundary - a next-earnings-date landing exactly on the
        // midnight-UTC instant 48 hours out still counts as active.
        using var dbContext = InMemoryDbContextFactory.Create();
        var boundaryDate = DateOnly.FromDateTime(AsOf.Date.AddHours(48));
        dbContext.RawEarningsObservations.Add(MakeObservation("AAPL", boundaryDate, AsOf.AddDays(-1), fetchOutcome: EarningsFetchOutcome.UpcomingEarningsFound));
        await dbContext.SaveChangesAsync();

        var service = new EarningsLookupService(dbContext);
        var status = await service.GetBlackoutStatusAsync("AAPL", AsOf.Date, CancellationToken.None);

        Assert.Equal(EarningsBlackoutStatus.BlackoutActive, status);
    }

    [Fact]
    public async Task GetBlackoutStatusAsync_NoRowAtAll_ReturnsUnverified()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var service = new EarningsLookupService(dbContext);
        var status = await service.GetBlackoutStatusAsync("ZZZZ", AsOf, CancellationToken.None);

        Assert.Equal(EarningsBlackoutStatus.Unverified, status);
    }

    [Fact]
    public async Task GetBlackoutStatusAsync_MostRecentRowIsUnknownOutcome_ReturnsUnverifiedNotClear()
    {
        // A row exists (the fetch was attempted) but genuinely failed to parse - distinct
        // codepath from "no row," both collapse to the same Unverified outcome per this
        // service's own contract. Distinct from the NoUpcomingEarningsInWindow test below,
        // which used to collapse into this identical Unverified outcome before the
        // 2026-08-07 tri-state fix - that's the actual bug being closed by this file's
        // changes.
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawEarningsObservations.Add(MakeObservation("AAPL", null, AsOf.AddDays(-1), fetchOutcome: EarningsFetchOutcome.Unknown));
        await dbContext.SaveChangesAsync();

        var service = new EarningsLookupService(dbContext);
        var status = await service.GetBlackoutStatusAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(EarningsBlackoutStatus.Unverified, status);
    }

    [Fact]
    public async Task GetBlackoutStatusAsync_MostRecentRowIsNoUpcomingEarningsInWindow_ReturnsClearNotUnverified()
    {
        // The actual fix (CLAUDE.md's "Earnings Verification Tri-State Fix," 2026-08-07):
        // before this change, a fresh row with genuinely nothing upcoming was stored as
        // Verified=false and this test would have asserted Unverified, silently capping every
        // such candidate at WATCH. It's now correctly distinguishable from a genuine failure
        // (the test immediately above) and resolves to Clear.
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawEarningsObservations.Add(MakeObservation(
            "AAPL", null, AsOf.AddDays(-1), fetchOutcome: EarningsFetchOutcome.NoUpcomingEarningsInWindow));
        await dbContext.SaveChangesAsync();

        var service = new EarningsLookupService(dbContext);
        var status = await service.GetBlackoutStatusAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(EarningsBlackoutStatus.Clear, status);
    }

    [Fact]
    public async Task GetBlackoutStatusAsync_NoUpcomingEarningsInWindowRowOlderThanSevenDays_ReturnsUnverified()
    {
        // The staleness TTL applies uniformly to both non-Unknown outcomes, not just
        // UpcomingEarningsFound - a stale "nothing was upcoming a week ago" row is no more
        // trustworthy today than a stale positive one would be.
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawEarningsObservations.Add(MakeObservation(
            "AAPL", null, AsOf.AddDays(-8), fetchOutcome: EarningsFetchOutcome.NoUpcomingEarningsInWindow));
        await dbContext.SaveChangesAsync();

        var service = new EarningsLookupService(dbContext);
        var status = await service.GetBlackoutStatusAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(EarningsBlackoutStatus.Unverified, status);
    }

    [Fact]
    public async Task GetBlackoutStatusAsync_VerifiedRowOlderThanSevenDays_ReturnsUnverified()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawEarningsObservations.Add(MakeObservation(
            "AAPL", DateOnly.FromDateTime(AsOf.AddDays(10)), AsOf.AddDays(-8), fetchOutcome: EarningsFetchOutcome.UpcomingEarningsFound));
        await dbContext.SaveChangesAsync();

        var service = new EarningsLookupService(dbContext);
        var status = await service.GetBlackoutStatusAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(EarningsBlackoutStatus.Unverified, status);
    }

    [Fact]
    public async Task GetBlackoutStatusAsync_DateAlreadyElapsedRelativeToAsOf_ReturnsClearNotBlackoutActive()
    {
        // A stale-but-still-within-TTL row whose captured date has since slipped into the past
        // relative to asOf - deliberately treated as Clear, not an eternal blackout. See
        // EarningsLookupService's own comment for the reasoning.
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawEarningsObservations.Add(MakeObservation(
            "AAPL", DateOnly.FromDateTime(AsOf.AddDays(-2)), AsOf.AddDays(-1), fetchOutcome: EarningsFetchOutcome.UpcomingEarningsFound));
        await dbContext.SaveChangesAsync();

        var service = new EarningsLookupService(dbContext);
        var status = await service.GetBlackoutStatusAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(EarningsBlackoutStatus.Clear, status);
    }

    [Fact]
    public async Task GetBlackoutStatusAsync_MultipleRows_UsesMostRecentByFetchedAtOnly()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        // Older row would resolve to BlackoutActive if it were (incorrectly) selected.
        dbContext.RawEarningsObservations.Add(MakeObservation(
            "AAPL", DateOnly.FromDateTime(AsOf.AddHours(10)), AsOf.AddDays(-6), fetchOutcome: EarningsFetchOutcome.UpcomingEarningsFound));
        // Most recent row resolves to Clear - this is the one that must win.
        dbContext.RawEarningsObservations.Add(MakeObservation(
            "AAPL", DateOnly.FromDateTime(AsOf.AddDays(20)), AsOf.AddDays(-1), fetchOutcome: EarningsFetchOutcome.UpcomingEarningsFound));
        await dbContext.SaveChangesAsync();

        var service = new EarningsLookupService(dbContext);
        var status = await service.GetBlackoutStatusAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(EarningsBlackoutStatus.Clear, status);
    }

    [Fact]
    public async Task GetBlackoutStatusAsync_OtherTickerHasBlackout_DoesNotAffectThisTicker()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawEarningsObservations.Add(MakeObservation(
            "MSFT", DateOnly.FromDateTime(AsOf.AddHours(12)), AsOf.AddDays(-1), fetchOutcome: EarningsFetchOutcome.UpcomingEarningsFound));
        await dbContext.SaveChangesAsync();

        var service = new EarningsLookupService(dbContext);
        var status = await service.GetBlackoutStatusAsync("AAPL", AsOf, CancellationToken.None);

        Assert.Equal(EarningsBlackoutStatus.Unverified, status);
    }
}
