using System.Net;
using Drps.Adjuster.Configuration;
using Drps.Adjuster.Sentiment;
using Drps.Execution;
using Drps.Execution.Alpaca;
using Drps.Execution.Firing;
using Drps.Execution.PreFire;
using Drps.Ingestion;
using Drps.Ingestion.Feeders;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Execution.Firing;

public class OrderFiringServiceTests
{
    // A confirmed Monday - avoids the non-weekday warning-log path in every test.
    private static readonly DateTime Monday = new(2026, 7, 20, 10, 0, 0);

    // Same shipped-values fixture as PreFireGateServiceTests/AdjusterSizingServiceTests.
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

    private static GateScore BuildGateScore(long id = 2, string ticker = "AAPL") => new()
    {
        Id = id,
        Ticker = ticker,
        ScanDate = Monday,
        Bucket = GateBucket.Buy,
        CompositeScore = 0.90m,
        DataAsOfDate = DateOnly.FromDateTime(Monday),
        CalculationVersion = 1,
        GateParameterVersion = 1
    };

    // ShareCount defaults to 10m (a whole number, no remainder) - CLAUDE.md's Execution
    // Layer: Third Correction fixed ShareCount from `long` to `decimal(18,9)`, so both the
    // >=1 and <1 branches are genuinely reachable again. AllocationDollarAmount no longer has
    // any bearing on quantity - it is used only by PreFireGateService's cash-floor check, so
    // its default here is arbitrary and deliberately unrelated to ShareCount x price.
    private static AdjusterAllocation BuildAllocation(
        long id = 1, long gateScoreId = 2, decimal allocationDollarAmount = 1000m, decimal shareCount = 10m, bool shareCapDeficient = false) => new()
    {
        Id = id,
        GateScoreId = gateScoreId,
        AllocationDollarAmount = allocationDollarAmount,
        AllocationPercent = 0.03m,
        ShareCount = shareCount,
        ShareCapDeficient = shareCapDeficient,
        AsOfTimestamp = Monday,
        AdjusterParameterVersion = 1,
        InsiderMultiplierApplied = 1.0m,
        InsiderDataUnverified = false,
        // Neutral by default, same reasoning as InsiderMultiplierApplied above - every
        // pre-existing test below predates the options-flow combiner leg and doesn't care
        // about it; a bare, unset decimal default here would be 0m, not 1.0m, which would
        // silently corrupt every existing CombinedMultiplier/FiredQuantity assertion in this
        // file the moment MultiSignalMultiplierCombiner.Combine gained its third element.
        OptionsFlowMultiplierApplied = 1.0m
    };

    // Real SentimentMultiplierService backed by a FakeHttpMessageHandler - matches
    // MarketauxSentimentClientTests/SentimentMultiplierServiceTests' own convention (no mocking
    // library anywhere in this codebase). `sentimentScore: null` produces the fail-closed
    // neutral path (a real "no matching entity" response, 200 + empty data array), same shape
    // as a genuine "Marketaux never mentioned this ticker" result.
    private static SentimentMultiplierService BuildSentimentService(decimal? sentimentScore, string ticker = "AAPL")
    {
        var fixture = sentimentScore is null
            ? """{"meta":{"found":0,"returned":0,"limit":3,"page":1},"data":[]}"""
            : $$"""{"meta":{"found":1,"returned":1,"limit":3,"page":1},"data":[{"entities":[{"symbol":"{{ticker}}","sentiment_score":{{sentimentScore.Value}}}]}]}""";

        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(fixture) });
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var marketauxOptions = Options.Create(new MarketauxOptions { ApiKey = "test-key" });
        var client = new MarketauxSentimentClient(httpClientFactory, marketauxOptions, NullLogger<MarketauxSentimentClient>.Instance);
        var sentimentOptions = Options.Create(new SentimentMultiplierOptions());
        return new SentimentMultiplierService(client, sentimentOptions, NullLogger<SentimentMultiplierService>.Instance);
    }

    private static OrderFiringService CreateService(
        FakeAlpacaTradingClient client,
        DrpsDbContext dbContext,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int maxOpensPerDay = 5,
        FakePushoverNotificationService? pushoverNotificationService = null,
        SentimentMultiplierService? sentimentMultiplierService = null)
    {
        var tracker = new KillSwitchTracker(dbContext, NullLogger<KillSwitchTracker>.Instance);
        var settings = Options.Create(new PreFireGateSettings { KillSwitchMaxOpensPerDay = maxOpensPerDay });
        var preFireGateService = new PreFireGateService(
            client, dbContext, tracker, settings, new FakePushoverNotificationService(),
            new InFlightPositionTracker(),
            NullLogger<PreFireGateService>.Instance, () => Monday);

        // Instant by default - production wiring relies on the real Task.Delay default; tests
        // override this so the sub-1-share branch's 5-minute follow-up resolves immediately.
        // sentimentMultiplierService defaults to a neutral (1.0x) fake so every pre-existing
        // test below, none of which cares about sentiment, sees unchanged behavior - baseQuantity
        // x 1.0 == baseQuantity. Same dbContext instance PreFireGateService/KillSwitchTracker
        // already use above - CLAUDE.md's 2026-07-31 audit, Gap 1's AmbiguousFireSkip writes
        // land in the same in-memory database a test can assert against directly.
        return new OrderFiringService(
            client, dbContext, preFireGateService, sentimentMultiplierService ?? BuildSentimentService(sentimentScore: null),
            pushoverNotificationService ?? new FakePushoverNotificationService(),
            NullLogger<OrderFiringService>.Instance, delay ?? ((_, _) => Task.CompletedTask));
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private static async Task<DrpsDbContext> SeededDbContextAsync()
    {
        var dbContext = InMemoryDbContextFactory.Create();
        dbContext.AdjusterParameters.Add(ActiveAdjusterParameters());
        await dbContext.SaveChangesAsync();
        return dbContext;
    }

    // --- Pre-fire gate rejection -------------------------------------------------------------

    [Fact]
    public async Task FireAsync_PreFireGateRejects_NeverCallsQuoteOrOrderMethods()
    {
        using var dbContext = await SeededDbContextAsync();
        var client = new FakeAlpacaTradingClient
        {
            ClockResult = _ => Task.FromResult(new AlpacaClockResult { Success = true, IsOpen = false })
        };
        var service = CreateService(client, dbContext);

        var result = await service.FireAsync(BuildGateScore(), BuildAllocation(), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.RejectedByPreFireGate, result.Outcome);
        Assert.Contains("MarketHours", result.Reason);
        Assert.DoesNotContain("GetLatestQuoteAsync", client.CalledMethods);
        Assert.DoesNotContain("PlaceOrderAsync", client.CalledMethods);
    }

    // --- Zero-quantity skip path ---------------------------------------------------------------

    [Fact]
    public async Task FireAsync_ShareCapDeficient_SkipsWithoutCallingQuoteOrOrderMethods()
    {
        using var dbContext = await SeededDbContextAsync();
        var client = new FakeAlpacaTradingClient();
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(client, dbContext, pushoverNotificationService: pushover);

        var result = await service.FireAsync(BuildGateScore(), BuildAllocation(shareCapDeficient: true), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.SkippedZeroQuantity, result.Outcome);
        Assert.DoesNotContain("GetLatestQuoteAsync", client.CalledMethods);
        Assert.DoesNotContain("PlaceOrderAsync", client.CalledMethods);
        // No real order was ever placed - never notification-worthy (CLAUDE.md's Execution
        // Layer: Ninth Design Decision, only a genuine Outcome.Fired notifies).
        Assert.Empty(pushover.SentMessages);
    }

    // --- Quote fetch failure ---------------------------------------------------------------

    [Fact]
    public async Task FireAsync_QuoteFetchFails_ReturnsQuoteFetchFailedWithoutCallingPlaceOrder()
    {
        using var dbContext = await SeededDbContextAsync();
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = false, ErrorMessage = "network error" })
        };
        var service = CreateService(client, dbContext);

        var result = await service.FireAsync(BuildGateScore(), BuildAllocation(), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.QuoteFetchFailed, result.Outcome);
        Assert.DoesNotContain("PlaceOrderAsync", client.CalledMethods);
    }

    // --- Whole-share-with-remainder branch --------------------------------------------------

    [Fact]
    public async Task FireAsync_WholeShareWithRemainder_FloorsQuantityAndRecordsDiscardedRemainder()
    {
        using var dbContext = await SeededDbContextAsync();
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-1", ClientOrderId = request.ClientOrderId, Status = "filled"
            })
        };
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(client, dbContext, pushoverNotificationService: pushover);

        // ShareCount=10.5 comes straight from AdjusterAllocation - no price-based recompute
        // involved in reaching this value at all.
        var result = await service.FireAsync(BuildGateScore(), BuildAllocation(shareCount: 10.5m), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        Assert.Equal(10m, result.FiredQuantity);
        Assert.Equal(0.5m, result.DiscardedFractionalRemainder);
        Assert.Equal("drps-2-1-1", result.ClientOrderId);

        // A genuine fire (IOC branch) is one of Pushover's three wired trigger points
        // (CLAUDE.md's Execution Layer: Ninth Design Decision).
        var notification = Assert.Single(pushover.SentMessages);
        Assert.Contains("OPEN", notification);
        Assert.Contains("AAPL", notification);
        Assert.Contains("order-1", notification);
    }

    [Fact]
    public async Task FireAsync_WholeShareOrder_UsesIocTimeInForceAndMarketableLimitPrice()
    {
        using var dbContext = await SeededDbContextAsync();
        AlpacaOrderRequest? capturedRequest = null;
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(new AlpacaOrderResult { Success = true, OrderId = "order-1", ClientOrderId = request.ClientOrderId, Status = "filled" });
            }
        };
        var service = CreateService(client, dbContext);

        // Default ShareCount (10m, a whole number - no remainder), deliberately distinct from
        // the with-remainder scenario above.
        await service.FireAsync(BuildGateScore(), BuildAllocation(), CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("ioc", capturedRequest!.TimeInForce);
        Assert.Equal("limit", capturedRequest.Type);
        Assert.Equal("buy", capturedRequest.Side);
        Assert.Equal(10m, capturedRequest.Quantity);
        // Price is still live from the quote: ask (100) + 0.25% = 100.25.
        Assert.Equal(100.25m, capturedRequest.LimitPrice);
        // A whole-share order must never trigger the 5-minute cancel-if-unfilled follow-up -
        // that behavior is exclusive to the sub-1-share Day branch.
        Assert.DoesNotContain("GetOrderByClientOrderIdAsync", client.CalledMethods);
        Assert.DoesNotContain("CancelOrderAsync", client.CalledMethods);
    }

    [Fact]
    public async Task FireAsync_AskDoesNotDivideEvenlyByBuffer_RoundsLimitPriceUpToTheCent()
    {
        // Ask=214.14 is the real IBM value that triggered today's rejected fire: unrounded,
        // 214.14 * (1 + 0.0025) = 214.67535 - a genuine sub-penny limit_price Alpaca rejected
        // with a 422 ("sub-penny increment does not fulfill minimum pricing criteria"). This
        // case would have failed against the pre-fix code (which sent 214.67535 verbatim).
        // Asserting 214.68 (not 214.67) proves the fix rounds UP, not down/truncated - a naive
        // truncation to 214.67 would be a marketable-but-wrong price below the intended buffer.
        using var dbContext = await SeededDbContextAsync();
        AlpacaOrderRequest? capturedRequest = null;
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 214.14m, Bid = 214.14m }),
            PlaceOrderResult = (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(new AlpacaOrderResult { Success = true, OrderId = "order-1", ClientOrderId = request.ClientOrderId, Status = "filled" });
            }
        };
        var service = CreateService(client, dbContext);

        await service.FireAsync(BuildGateScore(), BuildAllocation(), CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(214.68m, capturedRequest!.LimitPrice);
        // No sub-cent precision survived the fix - the value in whole cents is itself a whole
        // number. (Scale can legitimately come out as 0/1/2 depending on trailing zeros in the
        // exact decimal division, so this checks the invariant that actually matters rather
        // than asserting a specific Scale value.)
        Assert.Equal(0m, (capturedRequest.LimitPrice!.Value * 100m) % 1m);
    }

    // --- Sub-1-share branch ------------------------------------------------------------------
    //
    // Restored here (CLAUDE.md's Execution Layer: Third Correction) after being removed in the
    // prior task, when ShareCount was still `long` and this branch was genuinely unreachable.
    // Now that ShareCount is decimal(18,9) and AdjusterSizingService no longer truncates it,
    // these scenarios are real again - re-verified against the actual fixed code, not
    // re-added unchanged.

    [Fact]
    public async Task FireAsync_SubOneShareAllocation_FiresFullFractionalQuantityAsDayOrder()
    {
        using var dbContext = await SeededDbContextAsync();
        AlpacaOrderRequest? capturedRequest = null;
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (request, _) =>
            {
                capturedRequest = request;
                return Task.FromResult(new AlpacaOrderResult { Success = true, OrderId = "order-1", ClientOrderId = request.ClientOrderId, Status = "filled" });
            },
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult { Success = true, OrderId = "order-1", Status = "filled" })
        };
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(client, dbContext, pushoverNotificationService: pushover);

        var result = await service.FireAsync(BuildGateScore(), BuildAllocation(shareCount: 0.3m), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        Assert.Equal(0.3m, result.FiredQuantity);
        Assert.Null(result.DiscardedFractionalRemainder);
        Assert.NotNull(capturedRequest);
        Assert.Equal("day", capturedRequest!.TimeInForce);
        Assert.Equal(0.3m, capturedRequest.Quantity);

        // A genuine fire (sub-1-share fractional/Day branch) is also notification-worthy - the
        // same trigger point as the IOC branch, just the other quantity path.
        var notification = Assert.Single(pushover.SentMessages);
        Assert.Contains("OPEN", notification);
        Assert.Contains("AAPL", notification);
    }

    [Fact]
    public async Task FireAsync_SubOneShareOrder_FilledWithinFiveMinutes_DoesNotCancel()
    {
        using var dbContext = await SeededDbContextAsync();
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult { Success = true, OrderId = "order-1", ClientOrderId = request.ClientOrderId, Status = "accepted" }),
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult { Success = true, OrderId = "order-1", Status = "filled" })
        };
        var service = CreateService(client, dbContext);

        var result = await service.FireAsync(BuildGateScore(), BuildAllocation(shareCount: 0.3m), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        Assert.Contains("GetOrderByClientOrderIdAsync", client.CalledMethods);
        Assert.DoesNotContain("CancelOrderAsync", client.CalledMethods);
    }

    [Fact]
    public async Task FireAsync_SubOneShareOrder_UnfilledAfterFiveMinutes_CancelsTheOrder()
    {
        using var dbContext = await SeededDbContextAsync();
        string? canceledOrderId = null;
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult { Success = true, OrderId = "order-1", ClientOrderId = request.ClientOrderId, Status = "accepted" }),
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult { Success = true, OrderId = "order-1", Status = "accepted" }),
            CancelOrderResult = (orderId, _) =>
            {
                canceledOrderId = orderId;
                return Task.FromResult(new AlpacaCancelOrderResult { Success = true });
            }
        };
        var service = CreateService(client, dbContext);

        var result = await service.FireAsync(BuildGateScore(), BuildAllocation(shareCount: 0.3m), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        Assert.Contains("CancelOrderAsync", client.CalledMethods);
        Assert.Equal("order-1", canceledOrderId);
    }

    [Fact]
    public async Task FireAsync_SubOneShareOrder_WaitsUsingTheInjectedFiveMinuteDelay()
    {
        using var dbContext = await SeededDbContextAsync();
        TimeSpan? requestedDelay = null;
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult { Success = true, OrderId = "order-1", ClientOrderId = request.ClientOrderId, Status = "accepted" }),
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult { Success = true, OrderId = "order-1", Status = "filled" })
        };
        var service = CreateService(client, dbContext, delay: (delay, _) =>
        {
            requestedDelay = delay;
            return Task.CompletedTask;
        });

        await service.FireAsync(BuildGateScore(), BuildAllocation(shareCount: 0.3m), CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(5), requestedDelay);
    }

    // --- Retry classification: terminal 4xx -------------------------------------------------

    [Fact]
    public async Task FireAsync_TerminalRejection_DoesNotRetryOrCheckStatus()
    {
        using var dbContext = await SeededDbContextAsync();
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = false, IsAmbiguousFailure = false, ErrorMessage = "insufficient buying power"
            })
        };
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(client, dbContext, pushoverNotificationService: pushover);

        var result = await service.FireAsync(BuildGateScore(), BuildAllocation(), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.RejectedByBroker, result.Outcome);
        Assert.Equal("insufficient buying power", result.Reason);
        Assert.Equal(1, client.CalledMethods.Count(m => m == "PlaceOrderAsync"));
        Assert.DoesNotContain("GetOrderByClientOrderIdAsync", client.CalledMethods);
        // A clean, unambiguous broker rejection is not notification-worthy - only Fired and
        // AmbiguousUnresolved are (CLAUDE.md's Execution Layer: Ninth Design Decision, extended
        // 2026-07-31).
        Assert.Empty(pushover.SentMessages);
    }

    // --- Retry classification: ambiguous, found via status check ---------------------------

    [Fact]
    public async Task FireAsync_AmbiguousFailureFoundViaStatusCheck_TreatedAsFiredWithoutRetrying()
    {
        using var dbContext = await SeededDbContextAsync();
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = false, IsAmbiguousFailure = true, ErrorMessage = "timeout"
            }),
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-found", Status = "accepted"
            })
        };
        var service = CreateService(client, dbContext);

        var result = await service.FireAsync(BuildGateScore(), BuildAllocation(), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        Assert.Equal("order-found", result.OrderId);
        Assert.Equal(1, client.CalledMethods.Count(m => m == "PlaceOrderAsync"));
        Assert.Equal(1, client.CalledMethods.Count(m => m == "GetOrderByClientOrderIdAsync"));
    }

    // --- Retry classification: ambiguous, not found, retry succeeds ------------------------

    [Fact]
    public async Task FireAsync_AmbiguousFailureNotFound_RetriesOnceWithSameClientOrderIdAndSucceeds()
    {
        using var dbContext = await SeededDbContextAsync();
        var placeCallCount = 0;
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (request, _) =>
            {
                placeCallCount++;
                return placeCallCount == 1
                    ? Task.FromResult(new AlpacaOrderResult { Success = false, IsAmbiguousFailure = true, ErrorMessage = "timeout" })
                    : Task.FromResult(new AlpacaOrderResult { Success = true, OrderId = "order-retry", ClientOrderId = request.ClientOrderId, Status = "filled" });
            },
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult { Success = false, NotFound = true })
        };
        var service = CreateService(client, dbContext);

        var result = await service.FireAsync(BuildGateScore(), BuildAllocation(), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        Assert.Equal("order-retry", result.OrderId);
        Assert.Equal(2, placeCallCount);
        Assert.Equal(1, client.CalledMethods.Count(m => m == "GetOrderByClientOrderIdAsync"));
        Assert.Equal("drps-2-1-1", result.ClientOrderId);
    }

    // --- Retry classification: never retries more than once --------------------------------

    [Fact]
    public async Task FireAsync_AmbiguousFailureTwice_ReturnsAmbiguousUnresolvedWithoutASecondRetry()
    {
        using var dbContext = await SeededDbContextAsync();
        var placeCallCount = 0;
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (_, _) =>
            {
                placeCallCount++;
                return Task.FromResult(new AlpacaOrderResult { Success = false, IsAmbiguousFailure = true, ErrorMessage = "timeout" });
            },
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult { Success = false, NotFound = true })
        };
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(client, dbContext, pushoverNotificationService: pushover);

        var result = await service.FireAsync(BuildGateScore(), BuildAllocation(), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.AmbiguousUnresolved, result.Outcome);
        // Exactly two PlaceOrderAsync calls total (the original attempt + the one allowed
        // retry) - never a third.
        Assert.Equal(2, placeCallCount);
        Assert.Equal(1, client.CalledMethods.Count(m => m == "GetOrderByClientOrderIdAsync"));

        // The fourth Pushover trigger (2026-07-31 retry-ambiguity audit) - AmbiguousUnresolved
        // is exactly the "cannot confirm either way" case Pushover's alerting philosophy exists
        // for. Message must carry enough for Kent to act on without tailing logs: ticker,
        // clientOrderId, and the underlying reason.
        var notification = Assert.Single(pushover.SentMessages);
        Assert.Contains("OPEN", notification);
        Assert.Contains("AAPL", notification);
        Assert.Contains("AMBIGUOUS", notification);
        Assert.Contains(result.ClientOrderId!, notification);
        Assert.Contains("timeout", notification);
        Assert.Contains("AmbiguousUnresolved", notification);

        // CLAUDE.md's 2026-07-31 audit, Gap 1 - an AmbiguousFireSkip row must be written for
        // this ticker, unconsumed, so OrchestrationWorker's next candidate check for AAPL finds
        // it and skips that one attempt.
        var skip = await dbContext.AmbiguousFireSkips.SingleAsync(s => s.Ticker == "AAPL");
        Assert.Null(skip.ConsumedAt);
    }

    // --- Sentiment multiplier adjustment (Sentiment Adjuster Multiplier Decision, CLAUDE.md
    // 2026-07-24) ------------------------------------------------------------------------------

    [Fact]
    public async Task FireAsync_SentimentDataUnavailable_AppliesNeutralMultiplierAndReportsItExplicitly()
    {
        using var dbContext = await SeededDbContextAsync();
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-1", ClientOrderId = request.ClientOrderId, Status = "filled"
            })
        };
        // The default CreateService sentiment service already resolves to the fail-closed
        // neutral path (no matching entity) - used explicitly here rather than implicitly, so
        // this test documents exactly which scenario it exercises.
        var sentimentService = BuildSentimentService(sentimentScore: null);
        var service = CreateService(client, dbContext, sentimentMultiplierService: sentimentService);

        var result = await service.FireAsync(BuildGateScore(), BuildAllocation(shareCount: 10m), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        // Quantity is unchanged by a neutral multiplier...
        Assert.Equal(10m, result.FiredQuantity);
        // ...but BaseQuantity/SentimentMultiplier are still explicitly populated, never
        // null/missing, per this task's own explicit traceability requirement.
        Assert.Equal(10m, result.BaseQuantity);
        Assert.Equal(1.0m, result.SentimentMultiplier);
    }
}
