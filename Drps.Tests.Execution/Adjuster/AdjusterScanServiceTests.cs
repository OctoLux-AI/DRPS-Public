using System.Net;
using Drps.Adjuster.OptionsFlow;
using Drps.Adjuster.Positioning;
using Drps.Adjuster.Scoring;
using Drps.Adjuster.Sizing;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Ingestion.Persistence;
using Drps.Ingestion.Verification;
using Drps.Ledger;
using Drps.Shared.Models;
using Drps.Shared.Notifications;
using Drps.Shared.Positioning;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Adjuster;

public class AdjusterScanServiceTests
{
    private static readonly DateTime AsOf = new(2026, 7, 17, 12, 0, 0);
    private static readonly DateOnly AsOfDate = DateOnly.FromDateTime(AsOf);

    private const string AccountFixtureSuccess = """{"buying_power": "1000000.00"}""";

    private static RawOhlcvBar MakeBar(string symbol, decimal close) => new()
    {
        Source = SourceType.Alpaca,
        Symbol = symbol,
        Timestamp = new DateTimeOffset(AsOfDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        Resolution = "1Day",
        Open = close - 1m,
        High = close + 1m,
        Low = close - 2m,
        Close = close,
        Volume = 1_000_000,
        AdjustmentType = "raw",
        IngestedAt = DateTimeOffset.UtcNow,
        RequestId = Guid.NewGuid()
    };

    private static GateScore MakeBuyGateScore(string ticker, decimal compositeScore, string? sector = null) => new()
    {
        Ticker = ticker,
        Sector = sector,
        Bucket = GateBucket.Buy,
        CompositeScore = compositeScore,
        ScanDate = AsOf,
        CalculationVersion = 1,
        GateParameterVersion = 1
    };

    // [REDACTED FOR PUBLIC RELEASE] Placeholder fixture values, not DRPS's real shipped
    // tuning - see README.md's "What's intentionally not public" section.
    private static async Task<AdjusterParameters> SeedActiveAdjusterParametersAsync(DrpsDbContext dbContext)
    {
        var parameters = new AdjusterParameters
        {
            EffectiveFrom = AsOf.Date,
            IsActive = true,
            TierOneFloor = 0.9m,
            TierOneCeiling = 0.93m,
            TierTwoCeiling = 0.96m,
            TierOneBaseRate = 0.02m,
            TierTwoBaseRate = 0.03m,
            TierThreeBaseRate = 0.04m,
            SectorCapPercent = 0.25m,
            BaseReservePercent = 0.2m,
            ReserveStepPercent = 0.05m,
            ReserveMilestoneOne = 5000m,
            ReserveMilestoneTwo = 50000m
        };

        dbContext.AdjusterParameters.Add(parameters);
        await dbContext.SaveChangesAsync();

        return parameters;
    }

    // Same shape as the migration's SQL-level column defaults - fails multiple
    // AdjusterParametersValidator rules simultaneously, the real scenario the validator
    // exists to catch (see AdjusterParametersValidatorTests' own all-zero-row test).
    private static async Task SeedInvalidActiveAdjusterParametersAsync(DrpsDbContext dbContext)
    {
        var parameters = new AdjusterParameters
        {
            EffectiveFrom = AsOf.Date,
            IsActive = true,
            TierOneFloor = 0m,
            TierOneCeiling = 0m,
            TierTwoCeiling = 0m,
            TierOneBaseRate = 0m,
            TierTwoBaseRate = 0m,
            TierThreeBaseRate = 0m,
            SectorCapPercent = 0m,
            BaseReservePercent = 0m,
            ReserveStepPercent = 0m,
            ReserveMilestoneOne = 0m,
            ReserveMilestoneTwo = 0m
        };

        dbContext.AdjusterParameters.Add(parameters);
        await dbContext.SaveChangesAsync();
    }

    private static AlpacaAccountFeeder CreateAccountFeeder(bool succeeds)
    {
        var handler = succeeds
            ? new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AccountFixtureSuccess) })
            : new FakeHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"forbidden"}""") });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://paper-api.alpaca.markets") };
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var options = Options.Create(new AlpacaOptions { KeyId = "test-key-id", SecretKey = "test-secret" });

        return new AlpacaAccountFeeder(httpClientFactory, options, NullLogger<AlpacaAccountFeeder>.Instance);
    }

    private static AdjusterScanService CreateService(
        DrpsDbContext drpsDb,
        AlpacaAccountFeeder accountFeeder,
        IPortfolioStateProvider? portfolioStateProvider = null,
        CboeOptionsChainClient? optionsChainClient = null)
    {
        return new AdjusterScanService(
            drpsDb,
            new AdjusterSizingService(),
            accountFeeder,
            portfolioStateProvider ?? new StubPortfolioStateProvider(),
            new InsiderLookupService(drpsDb),
            // Defaults to a client whose HTTP call always fails - every pre-existing test in
            // this file predates the options-flow signal and doesn't care about it; a failing
            // fetch resolves to the neutral 1.0m multiplier (CboeOptionsChainClient/
            // OptionsFlowMultiplierService's own fail-closed contracts), leaving every existing
            // assertion below unaffected.
            optionsChainClient ?? CreateOptionsChainClient(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            Options.Create(new OptionsFlowMultiplierOptions()),
            // Real LedgerLifecycleStampService, same DrpsDbContext as the rest of the test -
            // position-count-cap displacement tests can inspect drpsDb.Positions afterward the
            // same way they already inspect AdjusterAllocations, rather than needing a fake.
            new LedgerLifecycleStampService(
                drpsDb, new NoOpLifecycleNotificationService(), NullLogger<LedgerLifecycleStampService>.Instance),
            NullLogger<AdjusterScanService>.Instance);
    }

    private static CboeOptionsChainClient CreateOptionsChainClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHttpMessageHandler(responder);
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        return new CboeOptionsChainClient(httpClientFactory, NullLogger<CboeOptionsChainClient>.Instance);
    }

    // Seeds a real, open Position (with its own GateScore/AdjusterAllocation FKs, matching
    // Position's real referential shape) for the position-count-cap displacement tests below -
    // these need an actual Position row to inspect DisplacementDate on afterward, unlike the
    // sector-cap/reserve-capital tests above, which only ever assert on AdjusterAllocations.
    private static async Task<Position> SeedOpenPositionForDisplacementAsync(DrpsDbContext dbContext, string ticker)
    {
        var gateScore = new GateScore
        {
            Ticker = ticker,
            Bucket = GateBucket.Buy,
            CompositeScore = 0.90m,
            ScanDate = AsOf,
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
            ShareCount = 300,
            ShareCapDeficient = false,
            AsOfTimestamp = AsOf,
            AdjusterParameterVersion = 1
        };
        dbContext.AdjusterAllocations.Add(allocation);
        await dbContext.SaveChangesAsync();

        var position = new Position
        {
            Ticker = ticker,
            GateScoreId = gateScore.Id,
            AdjusterAllocationId = allocation.Id,
            EntryDate = AsOf,
            EntryPrice = 100m,
            EntryQuantity = 300m
        };
        dbContext.Positions.Add(position);
        await dbContext.SaveChangesAsync();

        return position;
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    // Returns a fixed, caller-specified PortfolioState regardless of when it's called -
    // unlike StubPortfolioStateProvider (always zero/empty), used by the running-total tests
    // below to seed a real starting baseline close to a cap, so the scan-local running-total
    // gap (this task's own subject) is actually exercised.
    private sealed class FixedPortfolioStateProvider : IPortfolioStateProvider
    {
        private readonly PortfolioState _state;

        public FixedPortfolioStateProvider(PortfolioState state) => _state = state;

        public Task<PortfolioState> GetCurrentStateAsync(CancellationToken cancellationToken) => Task.FromResult(_state);
    }

    [Fact]
    public async Task RunScanAsync_NoActiveAdjusterParameters_AbortsWithZeroRows()
    {
        using var drpsDb = InMemoryDbContextFactory.Create();

        drpsDb.GateScores.Add(MakeBuyGateScore("AAA", 0.89m));
        drpsDb.RawOhlcvBars.Add(MakeBar("AAA", 100m));
        await drpsDb.SaveChangesAsync();

        var service = CreateService(drpsDb, CreateAccountFeeder(succeeds: true));
        await service.RunScanAsync(AsOf, CancellationToken.None);

        Assert.Empty(await drpsDb.AdjusterAllocations.ToListAsync());
    }

    [Fact]
    public async Task RunScanAsync_InvalidActiveAdjusterParameters_AbortsWithZeroRows()
    {
        using var drpsDb = InMemoryDbContextFactory.Create();

        await SeedInvalidActiveAdjusterParametersAsync(drpsDb);
        drpsDb.GateScores.Add(MakeBuyGateScore("AAA", 0.89m));
        drpsDb.RawOhlcvBars.Add(MakeBar("AAA", 100m));
        await drpsDb.SaveChangesAsync();

        var service = CreateService(drpsDb, CreateAccountFeeder(succeeds: true));
        await service.RunScanAsync(AsOf, CancellationToken.None);

        Assert.Empty(await drpsDb.AdjusterAllocations.ToListAsync());
    }

    [Fact]
    public async Task RunScanAsync_FailedAccountFetch_AbortsWithZeroRows()
    {
        using var drpsDb = InMemoryDbContextFactory.Create();

        await SeedActiveAdjusterParametersAsync(drpsDb);
        drpsDb.GateScores.Add(MakeBuyGateScore("AAA", 0.89m));
        drpsDb.RawOhlcvBars.Add(MakeBar("AAA", 100m));
        await drpsDb.SaveChangesAsync();

        var service = CreateService(drpsDb, CreateAccountFeeder(succeeds: false));
        await service.RunScanAsync(AsOf, CancellationToken.None);

        Assert.Empty(await drpsDb.AdjusterAllocations.ToListAsync());
    }

    [Fact]
    public async Task RunScanAsync_SleeperBucketGateScore_NeverProducesAnAdjusterAllocation()
    {
        // Sleeper Bucket (CLAUDE.md 2026-08-04) - purely observational, must never be sized
        // or fired. DiscoverUnallocatedBuyCandidatesAsync's own query
        // (`g.Bucket == GateBucket.Buy`) is an ALLOWLIST, not a Watch/Neutral/Exit/Sleeper
        // denylist - this test proves that structural exclusion holds for the new bucket
        // value specifically, not just by inspection of the query's source. A deliberately
        // high CompositeScore proves the exclusion is bucket-based, not score-based - nothing
        // here should come close to being sized regardless of how "good" the score looks.
        using var drpsDb = InMemoryDbContextFactory.Create();

        await SeedActiveAdjusterParametersAsync(drpsDb);
        var sleeperScore = new GateScore
        {
            Ticker = "SLP",
            Bucket = GateBucket.Sleeper,
            CompositeScore = 0.99m,
            ScanDate = AsOf,
            CalculationVersion = 1,
            GateParameterVersion = 1
        };
        drpsDb.GateScores.Add(sleeperScore);
        drpsDb.RawOhlcvBars.Add(MakeBar("SLP", 100m));
        await drpsDb.SaveChangesAsync();

        var service = CreateService(drpsDb, CreateAccountFeeder(succeeds: true));
        await service.RunScanAsync(AsOf, CancellationToken.None);

        Assert.Empty(await drpsDb.AdjusterAllocations.ToListAsync());
    }

    // 20 identical daily bars (Volume=10,000, Close=100 -> $1,000,000 dollar volume/day)
    // ending at AsOfDate - the same fixture shape InsiderLookupServiceTests uses, needed here
    // so a real, non-neutral insider multiplier can actually be computed end-to-end through
    // the full RunScanAsync pipeline.
    private static void SeedTwentyDaysOfBars(DrpsDbContext dbContext, string ticker)
    {
        for (var i = 0; i < 20; i++)
        {
            var date = AsOfDate.AddDays(-i);
            dbContext.RawOhlcvBars.Add(new RawOhlcvBar
            {
                Source = SourceType.Alpaca,
                Symbol = ticker,
                Timestamp = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                Resolution = "1Day",
                Open = 100m,
                High = 100m,
                Low = 100m,
                Close = 100m,
                Volume = 10_000,
                AdjustmentType = "raw",
                IngestedAt = DateTimeOffset.UtcNow,
                RequestId = Guid.NewGuid()
            });
        }
    }

    // Real confirmed CBOE delayed-quotes options shape (CBOE delayed-quotes options endpoint
    // audit, 2026-07-29) - one call contract (volume 100), one put contract (volume 40) ->
    // ratio 40/100 = 0.4 -> bullishness (1.0 - 0.4) / 1.0 = 0.6 -> multiplier
    // 1.0 + (1.3 - 1.0) x 0.6 = 1.18, against the default OptionsFlowMultiplierOptions this
    // file's CreateService helper wires in.
    private const string OptionsChainFixture = """
    {
        "data": {
            "options": [
                {"option": "AAA260729C00100000", "volume": 100.0},
                {"option": "AAA260729P00100000", "volume": 40.0}
            ]
        }
    }
    """;

    [Fact]
    public async Task RunScanAsync_AlreadyAllocatedBuyCandidate_IsSkippedNotResized()
    {
        using var drpsDb = InMemoryDbContextFactory.Create();

        var parameters = await SeedActiveAdjusterParametersAsync(drpsDb);
        var gateScore = MakeBuyGateScore("AAA", 0.89m);
        drpsDb.GateScores.Add(gateScore);
        drpsDb.RawOhlcvBars.Add(MakeBar("AAA", 100m));
        await drpsDb.SaveChangesAsync();

        var existingAllocation = new AdjusterAllocation
        {
            GateScoreId = gateScore.Id,
            AllocationPercent = 0.01m,
            AllocationDollarAmount = 999m,
            ShareCount = 9,
            ShareCapDeficient = false,
            AsOfTimestamp = AsOf.AddDays(-1),
            AdjusterParameterVersion = parameters.Id
        };
        drpsDb.AdjusterAllocations.Add(existingAllocation);
        await drpsDb.SaveChangesAsync();

        var service = CreateService(drpsDb, CreateAccountFeeder(succeeds: true));
        await service.RunScanAsync(AsOf, CancellationToken.None);

        // Still exactly one row - the pre-existing one, untouched - proving the candidate was
        // excluded from discovery entirely rather than re-sized on top of it.
        var allocation = Assert.Single(await drpsDb.AdjusterAllocations.ToListAsync());
        Assert.Equal(999m, allocation.AllocationDollarAmount);
        Assert.Equal(9, allocation.ShareCount);
    }

    // Concurrent-position-cap displacement - end-to-end RunScanAsync tests below. Default
    // (placeholder, redacted for public release - see README.md) AdjusterParameters values
    // apply throughout for MaxConcurrentPositions/ConcurrentPositionDisplacementMarginPercent,
    // since SeedActiveAdjusterParametersAsync doesn't set either explicitly.

    [Fact]
    public async Task RunScanAsync_AtConcurrentPositionCap_CandidateBelowRequiredMargin_NoDisplacement()
    {
        using var drpsDb = InMemoryDbContextFactory.Create();
        await SeedActiveAdjusterParametersAsync(drpsDb);

        var weakPosition = await SeedOpenPositionForDisplacementAsync(drpsDb, "WEAK");

        var candidateGateScore = MakeBuyGateScore("ALMOST", 0.82m);
        drpsDb.GateScores.Add(candidateGateScore);
        drpsDb.RawOhlcvBars.Add(MakeBar("ALMOST", 100m));
        await drpsDb.SaveChangesAsync();

        // The candidate's score falls short of the required displacement margin over the
        // weakest held position's score - the exact margin formula/value is redacted for
        // public release (see README.md), so this is a deliberately non-boundary case rather
        // than one straddling the real cutoff.
        var portfolioState = new PortfolioState(
            TotalDeployedCapital: 0m,
            DeployedCapitalBySector: new Dictionary<string, decimal>(),
            OpenPositionCount: 15,
            WeakestHeldPosition: new WeakestHeldPosition("WEAK", 0.80m));

        var service = CreateService(drpsDb, CreateAccountFeeder(succeeds: true), new FixedPortfolioStateProvider(portfolioState));
        await service.RunScanAsync(AsOf, CancellationToken.None);

        var unchanged = await drpsDb.Positions.SingleAsync(p => p.Id == weakPosition.Id);
        Assert.Null(unchanged.DisplacementDate);
    }

    [Fact]
    public async Task RunScanAsync_UnderConcurrentPositionCap_NoDisplacementEvenIfMarginWouldClear()
    {
        using var drpsDb = InMemoryDbContextFactory.Create();
        await SeedActiveAdjusterParametersAsync(drpsDb);

        var weakPosition = await SeedOpenPositionForDisplacementAsync(drpsDb, "WEAK");

        var candidateGateScore = MakeBuyGateScore("STRONG", 0.95m);
        drpsDb.GateScores.Add(candidateGateScore);
        drpsDb.RawOhlcvBars.Add(MakeBar("STRONG", 100m));
        await drpsDb.SaveChangesAsync();

        // 14 < the default 15-position cap - even a candidate that would easily clear the
        // margin must not trigger displacement while the account isn't actually at the cap.
        var portfolioState = new PortfolioState(
            TotalDeployedCapital: 0m,
            DeployedCapitalBySector: new Dictionary<string, decimal>(),
            OpenPositionCount: 14,
            WeakestHeldPosition: new WeakestHeldPosition("WEAK", 0.80m));

        var service = CreateService(drpsDb, CreateAccountFeeder(succeeds: true), new FixedPortfolioStateProvider(portfolioState));
        await service.RunScanAsync(AsOf, CancellationToken.None);

        var unchanged = await drpsDb.Positions.SingleAsync(p => p.Id == weakPosition.Id);
        Assert.Null(unchanged.DisplacementDate);
    }

    [Fact]
    public async Task RunScanAsync_WeakestHeldTickerMatchesCandidateOwnTicker_SkipsDisplacement()
    {
        using var drpsDb = InMemoryDbContextFactory.Create();
        await SeedActiveAdjusterParametersAsync(drpsDb);

        // The candidate's own ticker is also, somehow, the account's reported weakest holding -
        // displacing "itself" to make room for itself is nonsensical and must be skipped.
        var position = await SeedOpenPositionForDisplacementAsync(drpsDb, "SAME");

        var candidateGateScore = MakeBuyGateScore("SAME", 0.95m);
        drpsDb.GateScores.Add(candidateGateScore);
        drpsDb.RawOhlcvBars.Add(MakeBar("SAME", 100m));
        await drpsDb.SaveChangesAsync();

        var portfolioState = new PortfolioState(
            TotalDeployedCapital: 0m,
            DeployedCapitalBySector: new Dictionary<string, decimal>(),
            OpenPositionCount: 15,
            WeakestHeldPosition: new WeakestHeldPosition("SAME", 0.80m));

        var service = CreateService(drpsDb, CreateAccountFeeder(succeeds: true), new FixedPortfolioStateProvider(portfolioState));
        await service.RunScanAsync(AsOf, CancellationToken.None);

        var unchanged = await drpsDb.Positions.SingleAsync(p => p.Id == position.Id);
        Assert.Null(unchanged.DisplacementDate);
    }
}
