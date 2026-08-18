using Drps.Adjuster.Configuration;
using Drps.Adjuster.Sentiment;
using Drps.Execution;
using Drps.Execution.Alpaca;
using Drps.Execution.Firing;
using Drps.Execution.Notifications;
using Drps.Execution.PreFire;
using Drps.Gate.Scoring;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Ingestion.Persistence;
using Drps.Ledger;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests;

public class AtrRatchetMonitorWorkerTests
{
    // A confirmed Monday - same fixture as OrderFiringServiceTests/OrchestrationWorkerTests,
    // avoids any non-weekday warning-log path in KillSwitchTracker/PreFireGateService's
    // market-hours check.
    private static readonly DateTime Monday = new(2026, 7, 20, 10, 0, 0);

    private static DrpsDbContext CreateDbContext(string dbName) =>
        new(new DbContextOptionsBuilder<DrpsDbContext>().UseInMemoryDatabase(dbName).Options);

    // Real SentimentMultiplierService backed by a FakeHttpMessageHandler returning a "no
    // matching entity" (neutral, 1.0x) fixture - same construction shape as
    // OrderFiringServiceTests.BuildSentimentService, kept minimal here since this file never
    // exercises sentiment behavior directly (FireCloseAsync never calls it at all).
    private static SentimentMultiplierService BuildNeutralSentimentService()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"meta":{"found":0,"returned":0,"limit":3,"page":1},"data":[]}""")
        });
        var httpClient = new HttpClient(handler);
        var marketauxClient = new MarketauxSentimentClient(
            new SingleClientHttpClientFactory(httpClient),
            Options.Create(new MarketauxOptions { ApiKey = "test-key" }),
            NullLogger<MarketauxSentimentClient>.Instance);
        return new SentimentMultiplierService(
            marketauxClient, Options.Create(new SentimentMultiplierOptions()), NullLogger<SentimentMultiplierService>.Instance);
    }

    private sealed class SingleClientHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private static ServiceProvider BuildProvider(
        string dbName, FakeAlpacaTradingClient client, AtrMonitorSettings settings,
        FakePushoverNotificationService? pushoverNotificationService = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DrpsDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton<IAlpacaTradingClient>(client);
        services.AddSingleton(Options.Create(settings));
        // Singleton fake, same instance resolved everywhere in this provider - see
        // OrchestrationWorkerTests.BuildProvider's identical comment.
        services.AddSingleton<IPushoverNotificationService>(pushoverNotificationService ?? new FakePushoverNotificationService());

        services.AddScoped<KillSwitchTracker>();
        // AtrRatchetMonitorWorker's PreFireGateService only ever runs EvaluateCloseAsync, which
        // never calls EvaluateConcurrentPositionCapAsync at all (that check is open-side only) -
        // a fresh, otherwise-unused tracker is sufficient here, unlike OrchestrationWorkerTests'
        // shared-instance requirement.
        services.AddSingleton<IInFlightPositionTracker>(new InFlightPositionTracker());
        services.AddScoped(sp => new PreFireGateService(
            sp.GetRequiredService<IAlpacaTradingClient>(),
            sp.GetRequiredService<DrpsDbContext>(),
            sp.GetRequiredService<KillSwitchTracker>(),
            Options.Create(new PreFireGateSettings { KillSwitchMaxOpensPerDay = 100 }),
            sp.GetRequiredService<IPushoverNotificationService>(),
            sp.GetRequiredService<IInFlightPositionTracker>(),
            NullLogger<PreFireGateService>.Instance,
            () => Monday));
        // AtrRatchetMonitorWorker only ever calls FireCloseAsync, which never invokes
        // SentimentMultiplierService (Sentiment Adjuster Multiplier Decision, CLAUDE.md
        // 2026-07-24 - see FireCloseAsync's own comment) - a neutral fake is sufficient here,
        // this file doesn't exercise that invariant directly (OrderFiringServiceCloseTests does).
        services.AddSingleton(BuildNeutralSentimentService());
        services.AddScoped(sp => new OrderFiringService(
            sp.GetRequiredService<IAlpacaTradingClient>(),
            sp.GetRequiredService<DrpsDbContext>(),
            sp.GetRequiredService<PreFireGateService>(),
            sp.GetRequiredService<SentimentMultiplierService>(),
            sp.GetRequiredService<IPushoverNotificationService>(),
            NullLogger<OrderFiringService>.Instance,
            delay: (_, _) => Task.CompletedTask));

        services.AddScoped<LedgerPositionWriter>();
        services.AddScoped(sp => new FillConfirmationService(
            sp.GetRequiredService<IAlpacaTradingClient>(),
            sp.GetRequiredService<LedgerPositionWriter>(),
            NullLogger<FillConfirmationService>.Instance,
            delay: (_, _) => Task.CompletedTask,
            nowProvider: () => Monday,
            pollInterval: TimeSpan.FromMilliseconds(1)));

        return services.BuildServiceProvider();
    }

    // Seeds a real open Position with an explicit ratchet baseline (EntryAtr/TpTargetPrice/
    // HighWaterMark), via LedgerPositionWriter directly - same shape as
    // OrchestrationWorkerTests.SeedCloseCandidateAsync. The opening GateScore's own AtrValue is
    // deliberately NOT what AtrRatchetMonitorWorker reads for "current ATR" - a separate, later-
    // dated GateScore row (currentAtr) models Design Decision 4: current ATR always comes from
    // the ticker's MOST RECENT GateScore, never from the fixed EntryAtr snapshot.
    private static async Task<Position> SeedRatchetPositionAsync(
        DrpsDbContext dbContext, string ticker, decimal entryPrice, decimal entryAtr, decimal tpTargetPrice,
        decimal? highWaterMark, decimal currentAtr)
    {
        var openingGateScore = new GateScore
        {
            Ticker = ticker, Bucket = GateBucket.Buy, CompositeScore = 0.9m, AtrValue = entryAtr,
            ScanDate = Monday, CalculationVersion = 1, GateParameterVersion = 1
        };
        dbContext.GateScores.Add(openingGateScore);
        await dbContext.SaveChangesAsync();

        var allocation = new AdjusterAllocation
        {
            GateScoreId = openingGateScore.Id, AllocationPercent = 0.03m, AllocationDollarAmount = 1000m,
            ShareCount = 10m, ShareCapDeficient = false, AsOfTimestamp = Monday, AdjusterParameterVersion = 1
        };
        dbContext.AdjusterAllocations.Add(allocation);
        await dbContext.SaveChangesAsync();

        var writer = new LedgerPositionWriter(dbContext);
        var position = await writer.OpenPositionAsync(
            openingGateScore.Id, allocation.Id, ticker, Monday, entryPrice, entryQuantity: 10m, CancellationToken.None,
            openOrigin: PositionActionOrigin.Automated,
            entryAtr: entryAtr, tpTargetPrice: tpTargetPrice, highWaterMark: highWaterMark);

        // The "current, live" ATR - a distinct, later-dated GateScore row, per Design Decision 4.
        var currentGateScore = new GateScore
        {
            Ticker = ticker, Bucket = GateBucket.Buy, CompositeScore = 0.9m, AtrValue = currentAtr,
            ScanDate = Monday.AddHours(1), CalculationVersion = 1, GateParameterVersion = 1
        };
        dbContext.GateScores.Add(currentGateScore);
        await dbContext.SaveChangesAsync();

        return position;
    }

    private static FakeAlpacaTradingClient QuoteClient(decimal midpoint) => new()
    {
        QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult
        {
            Success = true, Bid = midpoint - 0.05m, Ask = midpoint + 0.05m
        })
    };

    private static AlpacaOrderResult FilledOrder(string clientOrderId, decimal quantity, decimal price) => new()
    {
        Success = true, OrderId = "order-" + clientOrderId, ClientOrderId = clientOrderId,
        Status = "filled", FilledQuantity = quantity, FilledAveragePrice = price
    };

    // --- High-water mark: rises on a new high, never falls on a lower price ------------------

    [Fact]
    public async Task RunCycleAsync_NewHighThenLowerPrice_UpdatesHighWaterMarkOnlyOnNewHigh()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        // EntryPrice=100, TpTargetPrice=110 (distance 10), currentAtr=5 - chosen so neither
        // cycle below breaches (stop level stays well under the test prices).
        await SeedRatchetPositionAsync(seedContext, "AAPL", entryPrice: 100m, entryAtr: 5m, tpTargetPrice: 110m,
            highWaterMark: 100m, currentAtr: 5m);

        var client = QuoteClient(105m); // new high: 105 > 100
        var provider = BuildProvider(dbName, client, new AtrMonitorSettings());
        var worker = new AtrRatchetMonitorWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<AtrMonitorSettings>>(),
            NullLogger<AtrRatchetMonitorWorker>.Instance, delay: (_, _) => Task.CompletedTask);

        var firstCycle = await worker.RunCycleAsync(CancellationToken.None);
        Assert.Empty(firstCycle); // no breach - nothing spawned

        var afterFirstCycle = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Ticker == "AAPL");
        Assert.Equal(105m, afterFirstCycle.HighWaterMark);

        // Second poll: price drops to 102 - still above 100 (original entry) but below the
        // now-105 high-water mark. Must NOT decrease it.
        client.QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Bid = 101.95m, Ask = 102.05m });
        var secondCycle = await worker.RunCycleAsync(CancellationToken.None);
        Assert.Empty(secondCycle);

        var afterSecondCycle = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Ticker == "AAPL");
        Assert.Equal(105m, afterSecondCycle.HighWaterMark);
        Assert.DoesNotContain(nameof(client.PlaceOrderAsync), client.CalledMethods);
    }

    // --- Multiplier interpolates correctly across the full progress range -------------------

    [Fact]
    public async Task RunCycleAsync_MultiplierInterpolatesAtProgressBoundaries()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        // EntryPrice=100, TpTargetPrice=110 (distance 10), currentAtr=1 (small, so the
        // tightening stop level never comes close to breaching across these three prices).
        await SeedRatchetPositionAsync(seedContext, "MSFT", entryPrice: 100m, entryAtr: 1m, tpTargetPrice: 110m,
            highWaterMark: 100m, currentAtr: 1m);

        var client = QuoteClient(100m); // progress = 0
        var provider = BuildProvider(dbName, client, new AtrMonitorSettings());
        var logger = new CapturingLogger<AtrRatchetMonitorWorker>();
        var worker = new AtrRatchetMonitorWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<AtrMonitorSettings>>(),
            logger, delay: (_, _) => Task.CompletedTask);

        await worker.RunCycleAsync(CancellationToken.None); // progress 0 -> multiplier 3.0
        Assert.Contains(logger.Messages, m => m.Contains("MSFT") && m.Contains("multiplier=3.0"));

        client.QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Bid = 104.95m, Ask = 105.05m });
        await worker.RunCycleAsync(CancellationToken.None); // progress 0.5 -> multiplier 2.0
        Assert.Contains(logger.Messages, m => m.Contains("MSFT") && m.Contains("multiplier=2.0"));

        client.QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Bid = 109.95m, Ask = 110.05m });
        await worker.RunCycleAsync(CancellationToken.None); // progress 1.0 -> multiplier 1.0
        Assert.Contains(logger.Messages, m => m.Contains("MSFT") && m.Contains("multiplier=1.0"));

        // None of these three polls ever breached.
        Assert.DoesNotContain(nameof(client.PlaceOrderAsync), client.CalledMethods);
    }

    // --- No breach when current price sits above the computed stop level --------------------

    [Fact]
    public async Task RunCycleAsync_PriceAboveStopLevel_NoBreach()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        // EntryPrice=100, TpTargetPrice=110 (distance 10), HighWaterMark seeded at 100,
        // currentAtr=5, currentPrice=105 -> a new high (HighWaterMark becomes 105),
        // progress=(105-100)/10=0.5, multiplier=3-(2*0.5)=2.0, stopLevel=105-(2*5)=95.
        // currentPrice(105) >= 95 -> comfortably above the stop level, no breach.
        await SeedRatchetPositionAsync(seedContext, "GOOG", entryPrice: 100m, entryAtr: 5m, tpTargetPrice: 110m,
            highWaterMark: 100m, currentAtr: 5m);

        var client = QuoteClient(105m);
        var provider = BuildProvider(dbName, client, new AtrMonitorSettings());
        var worker = new AtrRatchetMonitorWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<AtrMonitorSettings>>(),
            NullLogger<AtrRatchetMonitorWorker>.Instance, delay: (_, _) => Task.CompletedTask);

        var spawned = await worker.RunCycleAsync(CancellationToken.None);
        Assert.Empty(spawned);
        Assert.DoesNotContain(nameof(client.PlaceOrderAsync), client.CalledMethods);

        var stillOpen = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Ticker == "GOOG");
        Assert.Null(stillOpen.ExitDate);
    }

    // --- Genuine breach fires a real close, with ExitReason=AtrStop and the correct raw,
    // uncapped TpProgressAtExit ------------------------------------------------------------

    [Fact]
    public async Task RunCycleAsync_GenuineBreach_FiresCloseWithAtrStopAndCorrectTpProgressAtExit()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        // EntryPrice=100, TpTargetPrice=110 (distance 10), HighWaterMark seeded at 130
        // (already ratcheted up from a prior run), currentAtr=5, currentPrice=103 ->
        // rawProgress = (103-100)/10 = 0.3 exactly, multiplier = 3-(2*0.3) = 2.4,
        // stopLevel = 130 - (2.4*5) = 118. currentPrice(103) < 118 -> breach.
        var position = await SeedRatchetPositionAsync(seedContext, "TSLA", entryPrice: 100m, entryAtr: 5m,
            tpTargetPrice: 110m, highWaterMark: 130m, currentAtr: 5m);

        var client = QuoteClient(103m);
        client.PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult
        {
            Success = true, OrderId = "order-close-1", ClientOrderId = request.ClientOrderId, Status = "filled"
        });
        client.OrderByClientOrderIdResult = (clientOrderId, _) => Task.FromResult(FilledOrder(clientOrderId, 10m, 103m));

        var pushover = new FakePushoverNotificationService();
        var provider = BuildProvider(
            dbName, client, new AtrMonitorSettings { IsDryRunEnabled = false, FillConfirmationMaxWaitSeconds = 5 },
            pushoverNotificationService: pushover);
        var worker = new AtrRatchetMonitorWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<AtrMonitorSettings>>(),
            NullLogger<AtrRatchetMonitorWorker>.Instance, delay: (_, _) => Task.CompletedTask);

        var spawned = await worker.RunCycleAsync(CancellationToken.None);
        _ = Assert.Single(spawned);
        await Task.WhenAll(spawned);

        var closed = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Id == position.Id);
        Assert.NotNull(closed.ExitDate);
        Assert.Equal(PositionExitReason.AtrStop, closed.ExitReason);
        Assert.Equal(0.3m, closed.TpProgressAtExit);
        Assert.Contains(nameof(client.PlaceOrderAsync), client.CalledMethods);
        Assert.Contains(nameof(client.GetOrderByClientOrderIdAsync), client.CalledMethods);

        // Confirms investigation finding: AtrRatchetMonitorWorker fires an ATR-stop close via
        // the exact same OrderFiringService.FireCloseAsync method ordinary Exit-bucket closes
        // use, so wiring Pushover into OrderFiringService alone (no separate ATR-specific call
        // site) already covers ATR breaches (CLAUDE.md's Execution Layer: Ninth Design Decision).
        var notification = Assert.Single(pushover.SentMessages);
        Assert.Contains("CLOSE", notification);
        Assert.Contains("TSLA", notification);
    }

    // --- Dry-run (default): a genuine breach is still detected and spawned, but logs only and
    // never calls FireCloseAsync -----------------------------------------------------------------

    [Fact]
    public async Task RunCycleAsync_GenuineBreach_DryRunEnabled_LogsWithoutFiring()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        // Same breach-triggering setup as the genuine-fire test above (rawProgress=0.3).
        var position = await SeedRatchetPositionAsync(seedContext, "AMD", entryPrice: 100m, entryAtr: 5m,
            tpTargetPrice: 110m, highWaterMark: 130m, currentAtr: 5m);

        var client = QuoteClient(103m);
        // AtrMonitorSettings() default - IsDryRunEnabled defaults to true, same safe-by-default
        // posture as OrchestrationSettings.
        var provider = BuildProvider(dbName, client, new AtrMonitorSettings());
        var logger = new CapturingLogger<AtrRatchetMonitorWorker>();
        var worker = new AtrRatchetMonitorWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<AtrMonitorSettings>>(),
            logger, delay: (_, _) => Task.CompletedTask);

        var spawned = await worker.RunCycleAsync(CancellationToken.None);
        _ = Assert.Single(spawned); // breach still detected and spawned - the gate lives inside the spawned task
        await Task.WhenAll(spawned);

        Assert.Contains(logger.Messages, m =>
            m.Contains("DRY-RUN") && m.Contains("CLOSE") && m.Contains("AMD") &&
            m.Contains(position.Id.ToString()) && m.Contains("AtrStop"));
        Assert.DoesNotContain(nameof(client.PlaceOrderAsync), client.CalledMethods);
        Assert.DoesNotContain(nameof(client.GetOrderByClientOrderIdAsync), client.CalledMethods);

        var stillOpen = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Id == position.Id);
        Assert.Null(stillOpen.ExitDate);
    }

    // --- Already in-flight (shared tracker): skipped without firing a second time -----------

    [Fact]
    public async Task RunCycleAsync_AlreadyInFlight_SkipsBreachWithoutFiring()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        // Same breach-triggering setup as the genuine-breach test above.
        await SeedRatchetPositionAsync(seedContext, "NFLX", entryPrice: 100m, entryAtr: 5m,
            tpTargetPrice: 110m, highWaterMark: 130m, currentAtr: 5m);

        var client = QuoteClient(103m);
        var provider = BuildProvider(dbName, client, new AtrMonitorSettings());

        var sharedTracker = new InFlightPositionTracker();
        // Simulate OrchestrationWorker already being mid-close on this exact ticker right now.
        Assert.True(sharedTracker.TryMarkInFlight("NFLX"));

        var worker = new AtrRatchetMonitorWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<AtrMonitorSettings>>(),
            NullLogger<AtrRatchetMonitorWorker>.Instance, delay: (_, _) => Task.CompletedTask, inFlightTracker: sharedTracker);

        var spawned = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(spawned);
        Assert.DoesNotContain(nameof(client.PlaceOrderAsync), client.CalledMethods);

        var stillOpen = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Ticker == "NFLX");
        Assert.Null(stillOpen.ExitDate);
    }

    // --- Missing ratchet baseline: skipped with a log, never an exception --------------------

    [Fact]
    public async Task RunCycleAsync_PositionMissingEntryAtrAndTpTargetPrice_SkippedWithLogNotException()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);

        var gateScore = new GateScore
        {
            Ticker = "IBM", Bucket = GateBucket.Buy, CompositeScore = 0.9m, AtrValue = 5m,
            ScanDate = Monday, CalculationVersion = 1, GateParameterVersion = 1
        };
        seedContext.GateScores.Add(gateScore);
        await seedContext.SaveChangesAsync();

        var allocation = new AdjusterAllocation
        {
            GateScoreId = gateScore.Id, AllocationPercent = 0.03m, AllocationDollarAmount = 1000m,
            ShareCount = 10m, ShareCapDeficient = false, AsOfTimestamp = Monday, AdjusterParameterVersion = 1
        };
        seedContext.AdjusterAllocations.Add(allocation);
        await seedContext.SaveChangesAsync();

        // Manual-entry-shaped open: no entryAtr/tpTargetPrice/highWaterMark supplied at all -
        // the legacy/manually-seeded row this worker's scope check exists to skip. OpenOrigin is
        // Manual to match - this is deliberately simulating a manually-opened position, per
        // CLAUDE.md's Execution Layer: Tenth Design Decision.
        var writer = new LedgerPositionWriter(seedContext);
        var position = await writer.OpenPositionAsync(
            gateScore.Id, allocation.Id, "IBM", Monday, entryPrice: 100m, entryQuantity: 10m, CancellationToken.None,
            openOrigin: PositionActionOrigin.Manual);

        var client = QuoteClient(100m);
        var provider = BuildProvider(dbName, client, new AtrMonitorSettings());
        var logger = new CapturingLogger<AtrRatchetMonitorWorker>();
        var worker = new AtrRatchetMonitorWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<AtrMonitorSettings>>(),
            logger, delay: (_, _) => Task.CompletedTask);

        var spawned = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(spawned);
        Assert.Contains(logger.Messages, m => m.Contains("IBM") && m.Contains("missing EntryAtr/TpTargetPrice"));
        // The quote client is never even called for a position that's skipped before any quote
        // fetch would occur.
        Assert.DoesNotContain(nameof(client.GetLatestQuoteAsync), client.CalledMethods);

        var stillOpen = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Id == position.Id);
        Assert.Null(stillOpen.ExitDate);
        Assert.Null(stillOpen.HighWaterMark);
    }

    // --- Rejection-row filtering (CLAUDE.md's "Gate: Rejection Reasons Now Persisted,"
    // 2026-08-06): the live-ATR lookup must skip rejection rows exactly like it skips a
    // missing GateScore altogether, and must keep using the most recent REAL row when a
    // later-dated rejection row exists for the same ticker -------------------------------

    [Fact]
    public async Task RunCycleAsync_OnlyGateScoreForTickerIsRejectionRow_SkippedWithLogNotExceptionSameAsNoGateScoreAtAll()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);

        // The one and only GateScore row for this ticker is a rejection row (Bucket=Neutral,
        // RejectionReason set) - simulates a Position whose ticker has since been evaluated
        // and rejected by Gate on every subsequent scan (the real WFC pattern from the
        // pre-implementation audit), with no real scored row ever having existed for it.
        var rejectionScore = new GateScore
        {
            Ticker = "SBUX", Bucket = GateBucket.Neutral, CompositeScore = 0m, AtrValue = 5m,
            RejectionReason = nameof(GateRejectionReason.DmaNotAligned),
            ScanDate = Monday, CalculationVersion = 1, GateParameterVersion = 1
        };
        seedContext.GateScores.Add(rejectionScore);
        await seedContext.SaveChangesAsync();

        var allocation = new AdjusterAllocation
        {
            GateScoreId = rejectionScore.Id, AllocationPercent = 0.03m, AllocationDollarAmount = 1000m,
            ShareCount = 10m, ShareCapDeficient = false, AsOfTimestamp = Monday, AdjusterParameterVersion = 1
        };
        seedContext.AdjusterAllocations.Add(allocation);
        await seedContext.SaveChangesAsync();

        // A real ratchet baseline IS supplied here (unlike the missing-baseline test above) -
        // this position must reach and exercise the GateScore lookup itself, not be skipped
        // one check earlier.
        var writer = new LedgerPositionWriter(seedContext);
        var position = await writer.OpenPositionAsync(
            rejectionScore.Id, allocation.Id, "SBUX", Monday, entryPrice: 100m, entryQuantity: 10m, CancellationToken.None,
            openOrigin: PositionActionOrigin.Automated,
            entryAtr: 5m, tpTargetPrice: 110m, highWaterMark: 100m);

        var client = QuoteClient(100m);
        var provider = BuildProvider(dbName, client, new AtrMonitorSettings());
        var logger = new CapturingLogger<AtrRatchetMonitorWorker>();
        var worker = new AtrRatchetMonitorWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<AtrMonitorSettings>>(),
            logger, delay: (_, _) => Task.CompletedTask);

        var spawned = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(spawned);
        // Same message the "no GateScore row at all" path logs - proving the rejection row was
        // filtered out entirely, not merely deprioritized.
        Assert.Contains(logger.Messages, m => m.Contains("SBUX") && m.Contains("has no GateScore row at all"));
        Assert.DoesNotContain(nameof(client.GetLatestQuoteAsync), client.CalledMethods);

        var stillOpen = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Id == position.Id);
        Assert.Null(stillOpen.ExitDate);
        Assert.Equal(100m, stillOpen.HighWaterMark); // unchanged - never reached the quote/HWM step
    }

    [Fact]
    public async Task RunCycleAsync_LaterRejectionRowExistsForTicker_UsesOlderRealRowsAtrValueInstead()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);

        // EntryPrice=100, TpTargetPrice=110 (distance 10), HighWaterMark seeded at 110
        // (already ratcheted above today's quote), REAL currentAtr=5. currentPrice=103 is
        // below the seeded high, so HighWaterMark stays 110: rawProgress=(103-100)/10=0.3,
        // multiplier=3-(2*0.3)=2.4, stopLevel=110-(2.4*5)=98 -> currentPrice(103) >= 98, no
        // breach - this is the correct outcome using the real ATR.
        await SeedRatchetPositionAsync(seedContext, "SBUX", entryPrice: 100m, entryAtr: 5m, tpTargetPrice: 110m,
            highWaterMark: 110m, currentAtr: 5m);

        // A later-dated rejection row for the same ticker, carrying a deliberately WRONG,
        // much smaller AtrValue. If the RejectionReason==null filter were missing, this row
        // (most recent by ScanDate) would be picked up instead: stopLevel=110-(2.4*0.5)=108.8
        // -> currentPrice(103) < 108.8 -> a FALSE breach the fix must prevent.
        var laterRejectionScore = new GateScore
        {
            Ticker = "SBUX", Bucket = GateBucket.Neutral, CompositeScore = 0m, AtrValue = 0.5m,
            RejectionReason = nameof(GateRejectionReason.DmaNotAligned),
            ScanDate = Monday.AddHours(2), CalculationVersion = 1, GateParameterVersion = 1
        };
        seedContext.GateScores.Add(laterRejectionScore);
        await seedContext.SaveChangesAsync();

        var client = QuoteClient(103m);
        var provider = BuildProvider(dbName, client, new AtrMonitorSettings());
        var logger = new CapturingLogger<AtrRatchetMonitorWorker>();
        var worker = new AtrRatchetMonitorWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<AtrMonitorSettings>>(),
            logger, delay: (_, _) => Task.CompletedTask);

        var spawned = await worker.RunCycleAsync(CancellationToken.None);

        Assert.Empty(spawned); // no breach - proves the real 5.0 ATR was used, not the rejection row's 0.5
        Assert.Contains(logger.Messages, m => m.Contains("SBUX") && m.Contains("currentAtr=5"));
        Assert.DoesNotContain(nameof(client.PlaceOrderAsync), client.CalledMethods);

        var stillOpen = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Ticker == "SBUX");
        Assert.Null(stillOpen.ExitDate);
        Assert.Equal(110m, stillOpen.HighWaterMark); // unchanged - 103 never exceeded the seeded 110 high
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = new();
        private readonly object _lock = new();

        public IReadOnlyList<string> Messages
        {
            get { lock (_lock) { return _messages.ToList(); } }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_lock)
            {
                _messages.Add(message);
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
