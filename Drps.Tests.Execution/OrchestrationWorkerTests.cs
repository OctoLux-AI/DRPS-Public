using Drps.Adjuster.Configuration;
using Drps.Adjuster.Sentiment;
using Drps.Execution;
using Drps.Execution.Alpaca;
using Drps.Execution.Candidates;
using Drps.Execution.Firing;
using Drps.Execution.Notifications;
using Drps.Execution.PreFire;
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

public class OrchestrationWorkerTests
{
    // A confirmed Monday - same fixture as OrderFiringServiceTests, avoids any non-weekday
    // warning-log path in KillSwitchTracker.
    private static readonly DateTime Monday = new(2026, 7, 20, 10, 0, 0);

    private static AdjusterParameters ActiveAdjusterParameters() => new()
    {
        IsActive = true,
        TierOneFloor = 0.85m,
        TierOneCeiling = 0.89m,
        TierTwoCeiling = 0.93m,
        TierOneBaseRate = 0.03m,
        TierTwoBaseRate = 0.04m,
        TierThreeBaseRate = 0.05m,
        SectorCapPercent = 0.30m,
        BaseReservePercent = 0.25m,
        ReserveStepPercent = 0.10m,
        ReserveMilestoneOne = 10000m,
        ReserveMilestoneTwo = 100000m
    };

    private static DrpsDbContext CreateDbContext(string dbName) =>
        new(new DbContextOptionsBuilder<DrpsDbContext>().UseInMemoryDatabase(dbName).Options);

    // Real SentimentMultiplierService backed by a FakeHttpMessageHandler returning a "no
    // matching entity" (neutral, 1.0x) fixture - this file exercises real FireAsync opens via
    // OrchestrationWorker's full pipeline, so a genuinely callable (not throws-if-called)
    // service is required here, unlike AtrRatchetMonitorWorkerTests (close-only).
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
        string dbName, FakeAlpacaTradingClient client, OrchestrationSettings settings,
        FakePushoverNotificationService? pushoverNotificationService = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<DrpsDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddSingleton<IAlpacaTradingClient>(client);
        services.AddSingleton(Options.Create(settings));
        // Singleton fake, same instance resolved everywhere in this provider - unlike
        // production's Scoped registration, this fake holds no scoped dependencies, so a single
        // shared instance lets a test retrieve it and assert on SentMessages after the fact.
        services.AddSingleton<IPushoverNotificationService>(pushoverNotificationService ?? new FakePushoverNotificationService());

        // Fixed clock, same as PreFireGateService/FillConfirmationService below - every
        // GateScore.ScanDate seeded in this file is Monday, so OpenCandidateQuery's staleness
        // gate (CLAUDE.md's Execution Layer: Seventh Design Decision) must evaluate freshness
        // against that same fixed moment, not the real wall clock.
        services.AddScoped(sp => new OpenCandidateQuery(
            sp.GetRequiredService<DrpsDbContext>(),
            NullLogger<OpenCandidateQuery>.Instance,
            () => Monday));
        services.AddScoped<CloseCandidateQuery>();
        services.AddScoped<PlateauDeactivationCandidateQuery>();
        services.AddScoped<CandidateOrchestrator>();

        services.AddScoped<KillSwitchTracker>();
        // Singleton, same instance PreFireGateService (below) and every OrchestrationWorker
        // constructed against this provider in the tests below must share - the whole point of
        // the concurrent-position-cap reservation fix is that PreFireGateService's check and
        // OrchestrationWorker's per-ticker release (its existing finally block) operate on the
        // SAME tracker instance. A test that constructs OrchestrationWorker without explicitly
        // passing this same instance would silently defeat that sharing.
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
        // Sentiment Adjuster Multiplier Decision (CLAUDE.md 2026-07-24) - Singleton, same
        // registration shape as IPushoverNotificationService immediately above (no scoped
        // dependencies of its own).
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

    private static async Task<(GateScore GateScore, AdjusterAllocation Allocation)> SeedOpenCandidateAsync(
        DrpsDbContext dbContext, string ticker, decimal shareCount = 10m, decimal atrValue = 3m)
    {
        var gateScore = new GateScore
        {
            Ticker = ticker, Bucket = GateBucket.Buy, CompositeScore = 0.9m, AtrValue = atrValue,
            ScanDate = Monday, CalculationVersion = 1, GateParameterVersion = 1
        };
        dbContext.GateScores.Add(gateScore);
        await dbContext.SaveChangesAsync();

        var allocation = new AdjusterAllocation
        {
            GateScoreId = gateScore.Id, AllocationPercent = 0.03m, AllocationDollarAmount = 1000m,
            ShareCount = shareCount, ShareCapDeficient = false, AsOfTimestamp = Monday, AdjusterParameterVersion = 1,
            // Neutral (1.0) by default - AdjusterAllocation has no property default of its own,
            // so an unset value here is 0m, which is not neutral and would incorrectly engage
            // MultiSignalMultiplierCombiner's real (redacted) combination path in every test
            // using this helper, none of which care about multiplier behavior.
            InsiderMultiplierApplied = 1.0m,
            OptionsFlowMultiplierApplied = 1.0m
        };
        dbContext.AdjusterAllocations.Add(allocation);
        await dbContext.SaveChangesAsync();

        return (gateScore, allocation);
    }

    // Seeds a real open Position (via LedgerPositionWriter, same as production would create it),
    // optionally paired with the separate exit-signal GateScore (Bucket.Exit) CloseCandidateQuery
    // joins on and/or a DeactivatedDate stamp PlateauDeactivationCandidateQuery filters on -
    // Position.GateScoreId always references the ORIGINAL opening decision, a different row
    // than the exit-signal GateScore. Defaults reproduce the original composite-degradation-only
    // shape every pre-existing call site here relies on; withExitSignalGateScore=false and/or a
    // non-null deactivatedDate let a test exercise the plateau-only and both-flags-true (overlap)
    // cases CLAUDE.md's Execution Layer: Candidate Orchestrator Correction exists to cover.
    private static async Task<Position> SeedCloseCandidateAsync(
        DrpsDbContext dbContext, string ticker, decimal entryQuantity = 5m, decimal entryPrice = 200m,
        bool withExitSignalGateScore = true, DateTime? deactivatedDate = null)
    {
        var (openingGateScore, openingAllocation) = await SeedOpenCandidateAsync(dbContext, ticker, entryQuantity);

        var writer = new LedgerPositionWriter(dbContext);
        var position = await writer.OpenPositionAsync(
            openingGateScore.Id, openingAllocation.Id, ticker, Monday, entryPrice, entryQuantity, CancellationToken.None,
            PositionActionOrigin.Automated);

        if (deactivatedDate is not null)
        {
            position.DeactivatedDate = deactivatedDate;
            await dbContext.SaveChangesAsync();
        }

        if (withExitSignalGateScore)
        {
            var exitGateScore = new GateScore
            {
                Ticker = ticker, Bucket = GateBucket.Exit, CompositeScore = 0.5m, AtrValue = 2m,
                ScanDate = Monday, CalculationVersion = 1, GateParameterVersion = 1
            };
            dbContext.GateScores.Add(exitGateScore);
            await dbContext.SaveChangesAsync();
        }

        return position;
    }

    private static AlpacaOrderResult FilledOrder(string clientOrderId, decimal quantity, decimal price) => new()
    {
        Success = true, OrderId = "order-" + clientOrderId, ClientOrderId = clientOrderId,
        Status = "filled", FilledQuantity = quantity, FilledAveragePrice = price
    };

    // --- Dry-run: opens -----------------------------------------------------------------------

    [Fact]
    public async Task RunCycleAsync_DryRunOpenCandidate_LogsExpectedDetailsAndNeverCallsFireOrConfirm()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        seedContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await seedContext.SaveChangesAsync();
        var (gateScore, allocation) = await SeedOpenCandidateAsync(seedContext, "AAPL");

        var client = new FakeAlpacaTradingClient();
        var provider = BuildProvider(dbName, client, new OrchestrationSettings { IsDryRunEnabled = true });
        var logger = new CapturingLogger<OrchestrationWorker>();
        var worker = new OrchestrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<OrchestrationSettings>>(),
            logger, delay: (_, _) => Task.CompletedTask,
            inFlightTracker: provider.GetRequiredService<IInFlightPositionTracker>());

        var spawned = await worker.RunCycleAsync(CancellationToken.None);
        await Task.WhenAll(spawned);

        Assert.Contains(logger.Messages, m =>
            m.Contains("DRY-RUN") && m.Contains("OPEN") && m.Contains("AAPL") &&
            m.Contains(gateScore.Id.ToString()) && m.Contains(allocation.Id.ToString()));
        Assert.Empty(client.CalledMethods);
        Assert.Empty(await seedContext.Positions.ToListAsync());
    }

    // --- Dry-run: closes -----------------------------------------------------------------------

    [Fact]
    public async Task RunCycleAsync_DryRunCloseCandidate_LogsExpectedDetailsAndNeverCallsFireOrConfirm()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        seedContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await seedContext.SaveChangesAsync();
        var position = await SeedCloseCandidateAsync(seedContext, "MSFT", entryQuantity: 5m, entryPrice: 200m);

        var client = new FakeAlpacaTradingClient();
        var provider = BuildProvider(dbName, client, new OrchestrationSettings { IsDryRunEnabled = true });
        var logger = new CapturingLogger<OrchestrationWorker>();
        var worker = new OrchestrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<OrchestrationSettings>>(),
            logger, delay: (_, _) => Task.CompletedTask,
            inFlightTracker: provider.GetRequiredService<IInFlightPositionTracker>());

        var spawned = await worker.RunCycleAsync(CancellationToken.None);
        await Task.WhenAll(spawned);

        Assert.Contains(logger.Messages, m =>
            m.Contains("DRY-RUN") && m.Contains("CLOSE") && m.Contains("MSFT") &&
            m.Contains(position.Id.ToString()) && m.Contains("5") && m.Contains("200"));
        Assert.Empty(client.CalledMethods);

        var stillOpen = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Id == position.Id);
        Assert.Null(stillOpen.ExitDate);
    }

    // --- Dry-run: releases in-flight immediately --------------------------------------------

    [Fact]
    public async Task RunCycleAsync_DryRun_ReleasesInFlightImmediately()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        seedContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await seedContext.SaveChangesAsync();
        await SeedOpenCandidateAsync(seedContext, "AAPL");

        var client = new FakeAlpacaTradingClient();
        var provider = BuildProvider(dbName, client, new OrchestrationSettings { IsDryRunEnabled = true });
        var worker = new OrchestrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<OrchestrationSettings>>(),
            NullLogger<OrchestrationWorker>.Instance, delay: (_, _) => Task.CompletedTask,
            inFlightTracker: provider.GetRequiredService<IInFlightPositionTracker>());

        var firstCycle = await worker.RunCycleAsync(CancellationToken.None);
        await Task.WhenAll(firstCycle);

        // Same candidate still exists (dry-run never touches Position/GateScore) - a second,
        // immediate cycle must spawn a new task for it, proving the ticker was NOT left
        // in-flight after the first dry-run cycle completed.
        var secondCycle = await worker.RunCycleAsync(CancellationToken.None);

        _ = Assert.Single(secondCycle);
    }

    // --- Live mode: successful open ------------------------------------------------------------

    [Fact]
    public async Task RunCycleAsync_LiveOpenCandidate_FiresConfirmsWritesPositionAndClearsInFlight()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        seedContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await seedContext.SaveChangesAsync();
        await SeedOpenCandidateAsync(seedContext, "AAPL", shareCount: 10m, atrValue: 3m);

        var client = new FakeAlpacaTradingClient
        {
            PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-1", ClientOrderId = request.ClientOrderId, Status = "filled"
            }),
            OrderByClientOrderIdResult = (clientOrderId, _) => Task.FromResult(FilledOrder(clientOrderId, 10m, 150.25m))
        };
        var pushover = new FakePushoverNotificationService();
        var provider = BuildProvider(
            dbName, client, new OrchestrationSettings { IsDryRunEnabled = false, FillConfirmationMaxWaitSeconds = 5 },
            pushoverNotificationService: pushover);
        var worker = new OrchestrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<OrchestrationSettings>>(),
            NullLogger<OrchestrationWorker>.Instance, delay: (_, _) => Task.CompletedTask,
            inFlightTracker: provider.GetRequiredService<IInFlightPositionTracker>());

        var spawned = await worker.RunCycleAsync(CancellationToken.None);
        await Task.WhenAll(spawned);

        var written = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Ticker == "AAPL");
        Assert.Equal(150.25m, written.EntryPrice);
        Assert.Equal(10m, written.EntryQuantity);

        // End-to-end confirmation that OrchestrationWorker's real fire path notifies Pushover
        // (CLAUDE.md's Execution Layer: Ninth Design Decision) - direct coverage already exists
        // at OrderFiringServiceTests' unit level; this proves the wiring survives the full
        // worker->OrderFiringService call chain, not just the isolated method.
        var notification = Assert.Single(pushover.SentMessages);
        Assert.Contains("OPEN", notification);
        Assert.Contains("AAPL", notification);
        Assert.Equal(3m, written.EntryAtr);
        Assert.Contains("PlaceOrderAsync", client.CalledMethods);
        Assert.Contains("GetOrderByClientOrderIdAsync", client.CalledMethods);

        // In-flight cleared: a second cycle no longer even finds this ticker as a candidate
        // (OpenCandidateQuery excludes tickers with an open Position), which is only possible
        // if nothing about it (including the in-flight entry) is stuck.
        var secondCycle = await worker.RunCycleAsync(CancellationToken.None);
        Assert.Empty(secondCycle);
    }

    // --- AmbiguousFireSkip: one-time skip after a prior AmbiguousUnresolved outcome
    // (CLAUDE.md's 2026-07-31 retry-ambiguity audit, Gap 1) -------------------------------------

    [Fact]
    public async Task RunCycleAsync_OpenCandidateWithUnconsumedAmbiguousFireSkip_SkipsOnceWithoutFiringAndMarksConsumed()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        seedContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await seedContext.SaveChangesAsync();
        await SeedOpenCandidateAsync(seedContext, "AAPL", shareCount: 10m, atrValue: 3m);
        var skip = new AmbiguousFireSkip { Ticker = "AAPL", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        seedContext.AmbiguousFireSkips.Add(skip);
        await seedContext.SaveChangesAsync();

        // Deliberately no PlaceOrderResult/OrderByClientOrderIdResult configured - if the skip
        // check somehow failed to short-circuit and OrderFiringService actually reached
        // PlaceOrderAsync, FakeAlpacaTradingClient's own default behavior would surface that
        // clearly rather than this test silently passing for the wrong reason.
        var client = new FakeAlpacaTradingClient();
        var provider = BuildProvider(dbName, client, new OrchestrationSettings { IsDryRunEnabled = false, FillConfirmationMaxWaitSeconds = 5 });
        var logger = new CapturingLogger<OrchestrationWorker>();
        var worker = new OrchestrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<OrchestrationSettings>>(),
            logger, delay: (_, _) => Task.CompletedTask,
            inFlightTracker: provider.GetRequiredService<IInFlightPositionTracker>());

        var spawned = await worker.RunCycleAsync(CancellationToken.None);
        await Task.WhenAll(spawned);

        Assert.DoesNotContain("PlaceOrderAsync", client.CalledMethods);
        Assert.Empty(await seedContext.Positions.ToListAsync());

        var consumedSkip = await seedContext.AmbiguousFireSkips.AsNoTracking().SingleAsync(s => s.Id == skip.Id);
        Assert.NotNull(consumedSkip.ConsumedAt);

        Assert.Contains(logger.Messages, m =>
            m.Contains("AAPL") && m.Contains("skipped once") && m.Contains("AmbiguousFireSkip"));
    }

    [Fact]
    public async Task RunCycleAsync_OpenCandidateAfterAmbiguousFireSkipConsumed_FiresNormallyOnNextCycle()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        seedContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await seedContext.SaveChangesAsync();
        await SeedOpenCandidateAsync(seedContext, "AAPL", shareCount: 10m, atrValue: 3m);
        seedContext.AmbiguousFireSkips.Add(new AmbiguousFireSkip { Ticker = "AAPL", CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) });
        await seedContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient
        {
            PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-1", ClientOrderId = request.ClientOrderId, Status = "filled"
            }),
            OrderByClientOrderIdResult = (clientOrderId, _) => Task.FromResult(FilledOrder(clientOrderId, 10m, 150.25m))
        };
        var provider = BuildProvider(dbName, client, new OrchestrationSettings { IsDryRunEnabled = false, FillConfirmationMaxWaitSeconds = 5 });
        var worker = new OrchestrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<OrchestrationSettings>>(),
            NullLogger<OrchestrationWorker>.Instance, delay: (_, _) => Task.CompletedTask,
            inFlightTracker: provider.GetRequiredService<IInFlightPositionTracker>());

        // First cycle: unconsumed skip present - candidate skipped, not fired (same behavior as
        // the dedicated test above), and in-flight is released via the normal finally block.
        var firstCycle = await worker.RunCycleAsync(CancellationToken.None);
        await Task.WhenAll(firstCycle);
        Assert.Empty(await seedContext.Positions.ToListAsync());

        // Second cycle: the exact same GateScore/AdjusterAllocation pair still has no open
        // Position, so it's still a valid candidate - but the skip row is now consumed, so this
        // time it must fire for real. Single-use, no further gating (point 4's explicit
        // requirement).
        var secondCycle = await worker.RunCycleAsync(CancellationToken.None);
        await Task.WhenAll(secondCycle);

        var written = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Ticker == "AAPL");
        Assert.Equal(150.25m, written.EntryPrice);
        Assert.Contains("PlaceOrderAsync", client.CalledMethods);
    }

    [Fact]
    public async Task RunCycleAsync_OpenCandidateWithAlreadyConsumedAmbiguousFireSkip_FiresNormallyWithoutSkipping()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        seedContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await seedContext.SaveChangesAsync();
        await SeedOpenCandidateAsync(seedContext, "AAPL", shareCount: 10m, atrValue: 3m);
        // Already consumed from some earlier occurrence - must never gate a candidate again.
        seedContext.AmbiguousFireSkips.Add(new AmbiguousFireSkip
        {
            Ticker = "AAPL", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), ConsumedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
        await seedContext.SaveChangesAsync();

        var client = new FakeAlpacaTradingClient
        {
            PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-1", ClientOrderId = request.ClientOrderId, Status = "filled"
            }),
            OrderByClientOrderIdResult = (clientOrderId, _) => Task.FromResult(FilledOrder(clientOrderId, 10m, 150.25m))
        };
        var provider = BuildProvider(dbName, client, new OrchestrationSettings { IsDryRunEnabled = false, FillConfirmationMaxWaitSeconds = 5 });
        var worker = new OrchestrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<OrchestrationSettings>>(),
            NullLogger<OrchestrationWorker>.Instance, delay: (_, _) => Task.CompletedTask,
            inFlightTracker: provider.GetRequiredService<IInFlightPositionTracker>());

        var spawned = await worker.RunCycleAsync(CancellationToken.None);
        await Task.WhenAll(spawned);

        var written = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Ticker == "AAPL");
        Assert.Equal(150.25m, written.EntryPrice);
        Assert.Contains("PlaceOrderAsync", client.CalledMethods);
    }

    // --- Live mode: successful close (composite-degradation only) ----------------------------

    [Fact]
    public async Task RunCycleAsync_LiveCloseCandidate_FiresConfirmsClosesPositionAndClearsInFlight()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        seedContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await seedContext.SaveChangesAsync();
        var position = await SeedCloseCandidateAsync(seedContext, "MSFT", entryQuantity: 5m, entryPrice: 200m);

        var client = new FakeAlpacaTradingClient
        {
            PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-close-1", ClientOrderId = request.ClientOrderId, Status = "filled"
            }),
            OrderByClientOrderIdResult = (clientOrderId, _) => Task.FromResult(FilledOrder(clientOrderId, 5m, 210m))
        };
        var provider = BuildProvider(dbName, client, new OrchestrationSettings { IsDryRunEnabled = false, FillConfirmationMaxWaitSeconds = 5 });
        var worker = new OrchestrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<OrchestrationSettings>>(),
            NullLogger<OrchestrationWorker>.Instance, delay: (_, _) => Task.CompletedTask,
            inFlightTracker: provider.GetRequiredService<IInFlightPositionTracker>());

        var spawned = await worker.RunCycleAsync(CancellationToken.None);
        await Task.WhenAll(spawned);

        var closed = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Id == position.Id);
        Assert.NotNull(closed.ExitDate);
        Assert.Equal(210m, closed.ExitPrice);
        Assert.Equal(5m, closed.ExitQuantity);
        Assert.Equal(PositionExitReason.CompositeDegradation, closed.ExitReason);

        // In-flight cleared: CloseCandidateQuery itself no longer matches this ticker
        // (ExitDate is now set), but the seed helper's original Buy-bucket GateScore/
        // AdjusterAllocation pair legitimately resurfaces as a fresh OPEN candidate now that no
        // open Position blocks it (real, append-only-schema behavior, not a test artifact) -
        // so a second cycle spawning exactly one task for it is only possible if the ticker's
        // in-flight entry was actually released, not stuck from the close.
        var secondCycle = await worker.RunCycleAsync(CancellationToken.None);
        _ = Assert.Single(secondCycle);
    }

    // --- Live mode: successful close (plateau-deactivation only) -----------------------------

    [Fact]
    public async Task RunCycleAsync_LiveCloseCandidate_PlateauOnly_WritesPlateauDeactivationReason()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        seedContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await seedContext.SaveChangesAsync();
        var position = await SeedCloseCandidateAsync(
            seedContext, "MSFT", entryQuantity: 5m, entryPrice: 200m,
            withExitSignalGateScore: false, deactivatedDate: Monday.AddDays(-1));

        var client = new FakeAlpacaTradingClient
        {
            PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-close-1", ClientOrderId = request.ClientOrderId, Status = "filled"
            }),
            OrderByClientOrderIdResult = (clientOrderId, _) => Task.FromResult(FilledOrder(clientOrderId, 5m, 210m))
        };
        var provider = BuildProvider(dbName, client, new OrchestrationSettings { IsDryRunEnabled = false, FillConfirmationMaxWaitSeconds = 5 });
        var logger = new CapturingLogger<OrchestrationWorker>();
        var worker = new OrchestrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<OrchestrationSettings>>(),
            logger, delay: (_, _) => Task.CompletedTask,
            inFlightTracker: provider.GetRequiredService<IInFlightPositionTracker>());

        var spawned = await worker.RunCycleAsync(CancellationToken.None);
        await Task.WhenAll(spawned);

        var closed = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Id == position.Id);
        Assert.Equal(PositionExitReason.PlateauDeactivation, closed.ExitReason);

        // No overlap - the "flagged by BOTH" log must never appear for a plateau-only candidate.
        Assert.DoesNotContain(logger.Messages, m => m.Contains("flagged by BOTH"));
    }

    // --- Live mode: successful close (BOTH composite-degradation AND plateau) ----------------

    [Fact]
    public async Task RunCycleAsync_LiveCloseCandidate_BothCompositeDegradationAndPlateau_WritesCompositeDegradationAndLogsOverlap()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        seedContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await seedContext.SaveChangesAsync();
        // Both conditions genuinely apply at once - an Exit-bucket GateScore (CloseCandidateQuery)
        // AND a DeactivatedDate stamp (PlateauDeactivationCandidateQuery) on the same position -
        // the exact overlap CLAUDE.md's Execution Layer: Candidate Orchestrator Correction exists
        // to represent correctly rather than collapse.
        var position = await SeedCloseCandidateAsync(
            seedContext, "MSFT", entryQuantity: 5m, entryPrice: 200m,
            withExitSignalGateScore: true, deactivatedDate: Monday.AddDays(-1));

        var client = new FakeAlpacaTradingClient
        {
            PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-close-1", ClientOrderId = request.ClientOrderId, Status = "filled"
            }),
            OrderByClientOrderIdResult = (clientOrderId, _) => Task.FromResult(FilledOrder(clientOrderId, 5m, 210m))
        };
        var provider = BuildProvider(dbName, client, new OrchestrationSettings { IsDryRunEnabled = false, FillConfirmationMaxWaitSeconds = 5 });
        var logger = new CapturingLogger<OrchestrationWorker>();
        var worker = new OrchestrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<OrchestrationSettings>>(),
            logger, delay: (_, _) => Task.CompletedTask,
            inFlightTracker: provider.GetRequiredService<IInFlightPositionTracker>());

        var spawned = await worker.RunCycleAsync(CancellationToken.None);
        await Task.WhenAll(spawned);

        // Composite-degradation wins for the WRITTEN reason - the more specific, more recently-
        // triggered signal - but this must not be a silent resolution: the overlap itself must
        // be logged so the plateau signal isn't lost from the record entirely.
        var closed = await seedContext.Positions.AsNoTracking().SingleAsync(p => p.Id == position.Id);
        Assert.Equal(PositionExitReason.CompositeDegradation, closed.ExitReason);

        Assert.Contains(logger.Messages, m =>
            m.Contains("MSFT") && m.Contains("flagged by BOTH") &&
            m.Contains("composite-degradation") && m.Contains("plateau-deactivation"));
    }

    // --- Pre-fire-gate rejection stops before fill-confirmation ------------------------------

    [Fact]
    public async Task RunCycleAsync_PreFireGateRejection_StopsBeforeFillConfirmationAndClearsInFlight()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        seedContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await seedContext.SaveChangesAsync();
        await SeedOpenCandidateAsync(seedContext, "AAPL");

        var client = new FakeAlpacaTradingClient
        {
            // Market closed - rejected at the Market-Hours pre-fire gate, before any order or
            // fill-confirmation call is ever attempted.
            ClockResult = _ => Task.FromResult(new AlpacaClockResult { Success = true, IsOpen = false })
        };
        var provider = BuildProvider(dbName, client, new OrchestrationSettings { IsDryRunEnabled = false });
        var worker = new OrchestrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<OrchestrationSettings>>(),
            NullLogger<OrchestrationWorker>.Instance, delay: (_, _) => Task.CompletedTask,
            inFlightTracker: provider.GetRequiredService<IInFlightPositionTracker>());

        var spawned = await worker.RunCycleAsync(CancellationToken.None);
        await Task.WhenAll(spawned);

        Assert.DoesNotContain("PlaceOrderAsync", client.CalledMethods);
        Assert.DoesNotContain("GetOrderByClientOrderIdAsync", client.CalledMethods);
        Assert.Empty(await seedContext.Positions.ToListAsync());

        // In-flight cleared even on a pre-fire-gate rejection - the candidate is still an open
        // candidate (no Position was ever created), so a second cycle must spawn again.
        var secondCycle = await worker.RunCycleAsync(CancellationToken.None);
        _ = Assert.Single(secondCycle);
    }

    // --- Already in-flight: skipped without re-firing -----------------------------------------

    [Fact]
    public async Task RunCycleAsync_AlreadyInFlightCandidate_SkippedWithoutRefiringUntilPriorTaskCompletes()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        seedContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await seedContext.SaveChangesAsync();
        await SeedOpenCandidateAsync(seedContext, "AAPL");

        var placeOrderGate = new TaskCompletionSource<AlpacaOrderResult>();
        var placeOrderCallCount = 0;
        var client = new FakeAlpacaTradingClient
        {
            PlaceOrderResult = (request, _) =>
            {
                Interlocked.Increment(ref placeOrderCallCount);
                return placeOrderGate.Task;
            },
            OrderByClientOrderIdResult = (clientOrderId, _) => Task.FromResult(FilledOrder(clientOrderId, 10m, 150m))
        };
        var provider = BuildProvider(dbName, client, new OrchestrationSettings { IsDryRunEnabled = false, FillConfirmationMaxWaitSeconds = 5 });
        var worker = new OrchestrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<OrchestrationSettings>>(),
            NullLogger<OrchestrationWorker>.Instance, delay: (_, _) => Task.CompletedTask,
            inFlightTracker: provider.GetRequiredService<IInFlightPositionTracker>());

        // First cycle: spawns a task that blocks forever inside PlaceOrderAsync (still in-flight).
        var firstCycle = await worker.RunCycleAsync(CancellationToken.None);
        _ = Assert.Single(firstCycle);

        // Second cycle, while the first candidate's task is still pending: same ticker must be
        // skipped, not re-fired.
        var secondCycle = await worker.RunCycleAsync(CancellationToken.None);
        Assert.Empty(secondCycle);
        Assert.Equal(1, placeOrderCallCount);

        // Release the first task and let it finish.
        placeOrderGate.SetResult(new AlpacaOrderResult
        {
            Success = true, OrderId = "order-1", ClientOrderId = "drps-1-1-1", Status = "filled"
        });
        await Task.WhenAll(firstCycle);

        // Now the ticker has a real open Position - it's no longer an "open" candidate at all
        // (OpenCandidateQuery excludes it), confirming the earlier skip wasn't hiding a
        // duplicate-fire that only surfaces later.
        var thirdCycle = await worker.RunCycleAsync(CancellationToken.None);
        Assert.Empty(thirdCycle);
        Assert.Equal(1, placeOrderCallCount);
    }

    // --- One candidate's exception doesn't block a sibling in the same cycle -----------------

    [Fact]
    public async Task RunCycleAsync_ExceptionDuringOneCandidate_DoesNotBlockSiblingAndClearsInFlight()
    {
        var dbName = Guid.NewGuid().ToString();
        using var seedContext = CreateDbContext(dbName);
        seedContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await seedContext.SaveChangesAsync();
        await SeedOpenCandidateAsync(seedContext, "BADTICKER", shareCount: 10m);
        await SeedOpenCandidateAsync(seedContext, "GOODTICKER", shareCount: 10m);

        var client = new FakeAlpacaTradingClient
        {
            PlaceOrderResult = (request, _) =>
            {
                if (request.Symbol == "BADTICKER")
                {
                    throw new InvalidOperationException("simulated broker failure");
                }

                return Task.FromResult(new AlpacaOrderResult
                {
                    Success = true, OrderId = "order-good", ClientOrderId = request.ClientOrderId, Status = "filled"
                });
            },
            OrderByClientOrderIdResult = (clientOrderId, _) => Task.FromResult(FilledOrder(clientOrderId, 10m, 100m))
        };
        var provider = BuildProvider(dbName, client, new OrchestrationSettings { IsDryRunEnabled = false, FillConfirmationMaxWaitSeconds = 5 });
        var logger = new CapturingLogger<OrchestrationWorker>();
        var worker = new OrchestrationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IOptions<OrchestrationSettings>>(),
            logger, delay: (_, _) => Task.CompletedTask,
            inFlightTracker: provider.GetRequiredService<IInFlightPositionTracker>());

        var spawned = await worker.RunCycleAsync(CancellationToken.None);
        Assert.Equal(2, spawned.Count);

        // Must not throw - each candidate's exception is caught internally, never rethrown to
        // the caller.
        await Task.WhenAll(spawned);

        Assert.Contains(logger.Messages, m => m.Contains("BADTICKER") && m.Contains("unhandled exception"));
        Assert.Null(await seedContext.Positions.AsNoTracking().SingleOrDefaultAsync(p => p.Ticker == "BADTICKER"));
        Assert.NotNull(await seedContext.Positions.AsNoTracking().SingleOrDefaultAsync(p => p.Ticker == "GOODTICKER"));

        // BADTICKER's in-flight entry was still cleared despite the exception - it remains a
        // real open candidate (no Position was ever created for it), so a further cycle must
        // spawn a new task for it rather than skipping it as still in-flight.
        var nextCycle = await worker.RunCycleAsync(CancellationToken.None);
        _ = Assert.Single(nextCycle);
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
