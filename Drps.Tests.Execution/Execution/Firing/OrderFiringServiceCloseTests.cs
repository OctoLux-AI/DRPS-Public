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

// Close-side counterpart to OrderFiringServiceTests, mirroring its fixture/test shape exactly.
// Separate file/class rather than folded into OrderFiringServiceTests, since the two exercise
// genuinely different methods (FireAsync vs FireCloseAsync) with different fixtures
// (GateScore+AdjusterAllocation vs Position) - kept parallel, not merged, same reasoning as
// CloseCandidateQueryTests living alongside rather than inside OpenCandidateQueryTests.
public class OrderFiringServiceCloseTests
{
    // A confirmed Monday - avoids the non-weekday warning-log path in every test.
    private static readonly DateTime Monday = new(2026, 7, 20, 10, 0, 0);

    private static Position BuildPosition(long id = 1, string ticker = "AAPL", decimal entryQuantity = 10m) => new()
    {
        Id = id,
        Ticker = ticker,
        GateScoreId = 1,
        AdjusterAllocationId = 1,
        EntryDate = Monday.AddDays(-10),
        EntryPrice = 100m,
        EntryQuantity = entryQuantity
    };

    // Sentiment Adjuster Multiplier Decision (CLAUDE.md 2026-07-24): FireCloseAsync must never
    // call SentimentMultiplierService at all. Rather than build a passive fake that would
    // silently mask a real regression, this deliberately throws on the very first HTTP call it
    // would make - so if any FireCloseAsync code path ever starts calling it, EVERY close test
    // in this file fails loudly, not just the one dedicated test below.
    private static SentimentMultiplierService CreateSentimentServiceThatThrowsIfCalled(out FakeHttpMessageHandler handler)
    {
        handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("SentimentMultiplierService must never be called from FireCloseAsync"));
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
        // FireCloseAsync's own PreFireGateService never runs EvaluateConcurrentPositionCapAsync
        // (open-side only) - a fresh, otherwise-unused tracker is sufficient here.
        var preFireGateService = new PreFireGateService(
            client, dbContext, tracker, settings, new FakePushoverNotificationService(),
            new InFlightPositionTracker(),
            NullLogger<PreFireGateService>.Instance, () => Monday);

        // Same dbContext instance PreFireGateService/KillSwitchTracker already use above -
        // CLAUDE.md's 2026-07-31 audit, Gap 1's AmbiguousFireSkip writes (recorded for closes
        // too, per that decision's own reasoning, even though nothing reads them on this side).
        return new OrderFiringService(
            client, dbContext, preFireGateService,
            sentimentMultiplierService ?? CreateSentimentServiceThatThrowsIfCalled(out _),
            pushoverNotificationService ?? new FakePushoverNotificationService(),
            NullLogger<OrderFiringService>.Instance, delay ?? ((_, _) => Task.CompletedTask));
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    // No AdjusterParameters seeding here, unlike OrderFiringServiceTests' SeededDbContextAsync -
    // EvaluateCloseAsync never reads AdjusterParameters at all (no cash-floor check), so an
    // absent/missing active row must never block a close the way it would an open.
    private static DrpsDbContext EmptyDbContext() => InMemoryDbContextFactory.Create();

    // --- Pre-fire gate rejection -------------------------------------------------------------

    [Fact]
    public async Task FireCloseAsync_PreFireGateRejects_NeverCallsQuoteOrOrderMethods()
    {
        using var dbContext = EmptyDbContext();
        var client = new FakeAlpacaTradingClient
        {
            ClockResult = _ => Task.FromResult(new AlpacaClockResult { Success = true, IsOpen = false })
        };
        var pushover = new FakePushoverNotificationService();
        var service = CreateService(client, dbContext, pushoverNotificationService: pushover);

        var result = await service.FireCloseAsync(BuildPosition(), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.RejectedByPreFireGate, result.Outcome);
        Assert.Contains("MarketHours", result.Reason);
        Assert.DoesNotContain("GetLatestQuoteAsync", client.CalledMethods);
        Assert.DoesNotContain("PlaceOrderAsync", client.CalledMethods);
        // No real order was ever placed - never notification-worthy.
        Assert.Empty(pushover.SentMessages);
    }

    // --- Kill switch is never touched by a close ---------------------------------------------

    [Fact]
    public async Task FireCloseAsync_NeverIncrementsOrIsBlockedByKillSwitch()
    {
        // maxOpensPerDay: 0 would trip the kill switch instantly for an OPEN - confirms a
        // close is entirely unaffected by however exhausted the open-side budget already is.
        using var dbContext = EmptyDbContext();
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-1", ClientOrderId = request.ClientOrderId, Status = "filled"
            })
        };
        var service = CreateService(client, dbContext, maxOpensPerDay: 0);

        var result = await service.FireCloseAsync(BuildPosition(), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        // No KillSwitchCounter row was ever created - EvaluateCloseAsync never calls
        // KillSwitchTracker at all, confirmed directly rather than merely inferred from the
        // order having fired.
        Assert.False(await dbContext.KillSwitchCounters.AnyAsync());
    }

    // --- Quote fetch failure ---------------------------------------------------------------

    [Fact]
    public async Task FireCloseAsync_QuoteFetchFails_ReturnsQuoteFetchFailedWithoutCallingPlaceOrder()
    {
        using var dbContext = EmptyDbContext();
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = false, ErrorMessage = "network error" })
        };
        var service = CreateService(client, dbContext);

        var result = await service.FireCloseAsync(BuildPosition(), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.QuoteFetchFailed, result.Outcome);
        Assert.DoesNotContain("PlaceOrderAsync", client.CalledMethods);
    }

    // --- Whole-share-with-remainder branch --------------------------------------------------

    [Fact]
    public async Task FireCloseAsync_WholeShareWithRemainder_FloorsQuantityAndRecordsDiscardedRemainder()
    {
        using var dbContext = EmptyDbContext();
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

        var result = await service.FireCloseAsync(BuildPosition(id: 7, entryQuantity: 10.5m), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        Assert.Equal(10m, result.FiredQuantity);
        Assert.Equal(0.5m, result.DiscardedFractionalRemainder);
        Assert.Equal("drps-close-7-1", result.ClientOrderId);

        // A genuine close fire is also one of Pushover's three wired trigger points (CLAUDE.md's
        // Execution Layer: Ninth Design Decision) - this is also the exact path
        // AtrRatchetMonitorWorker's own ATR-breach closes go through, since it calls this same
        // FireCloseAsync method, confirming ATR breaches are covered with no separate call site.
        var notification = Assert.Single(pushover.SentMessages);
        Assert.Contains("CLOSE", notification);
        Assert.Contains("AAPL", notification);
        Assert.Contains("order-1", notification);
    }

    [Fact]
    public async Task FireCloseAsync_WholeShareOrder_UsesIocTimeInForceAndMarketableLimitPrice()
    {
        using var dbContext = EmptyDbContext();
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

        await service.FireCloseAsync(BuildPosition(entryQuantity: 10m), CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal("ioc", capturedRequest!.TimeInForce);
        Assert.Equal("limit", capturedRequest.Type);
        Assert.Equal("sell", capturedRequest.Side);
        Assert.Equal(10m, capturedRequest.Quantity);
        // Price is live from the quote: bid (99.9) - 0.25% = 99.65025, floored to the nearest
        // cent (never rounded up) per the Limit Price Rounding Defect fix - 99.65, not the
        // unrounded 99.65025 this test asserted before the fix, and not 99.66.
        Assert.Equal(99.65m, capturedRequest.LimitPrice);
        Assert.DoesNotContain("GetOrderByClientOrderIdAsync", client.CalledMethods);
        Assert.DoesNotContain("CancelOrderAsync", client.CalledMethods);
    }

    [Fact]
    public async Task FireCloseAsync_BidDoesNotDivideEvenlyByBuffer_FloorsLimitPriceDownToTheCent()
    {
        // Bid=214.14 mirrors the real IBM value that produced the rejected 214.67535
        // limit_price on the open side (Ask=214.14 there) - here, 214.14 * (1 - 0.0025) =
        // 213.60465, a genuine sub-penny value Alpaca would reject. Before the fix this went
        // out unrounded (213.60465); this case proves the fix, not just a round-number
        // fixture. Floor confirms the close side rounds DOWN (never up, which could push the
        // limit above the bid and make it non-marketable).
        using var dbContext = EmptyDbContext();
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

        await service.FireCloseAsync(BuildPosition(entryQuantity: 10m), CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(213.60m, capturedRequest!.LimitPrice);
        // No sub-cent precision survived the fix - the value in whole cents is itself a whole
        // number. (Scale can legitimately come out as 0/1/2 depending on trailing zeros in the
        // exact decimal division - e.g. 213.60m reduces to scale 1 - so this checks the
        // invariant that actually matters, not a specific Scale value.)
        Assert.Equal(0m, (capturedRequest.LimitPrice!.Value * 100m) % 1m);
    }

    // --- Sub-1-share branch ------------------------------------------------------------------

    [Fact]
    public async Task FireCloseAsync_SubOneShareHolding_FiresFullFractionalQuantityAsDayOrder()
    {
        using var dbContext = EmptyDbContext();
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
        var service = CreateService(client, dbContext);

        var result = await service.FireCloseAsync(BuildPosition(entryQuantity: 0.3m), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        Assert.Equal(0.3m, result.FiredQuantity);
        Assert.Null(result.DiscardedFractionalRemainder);
        Assert.NotNull(capturedRequest);
        Assert.Equal("day", capturedRequest!.TimeInForce);
        Assert.Equal("sell", capturedRequest.Side);
        Assert.Equal(0.3m, capturedRequest.Quantity);
    }

    [Fact]
    public async Task FireCloseAsync_SubOneShareOrder_FilledWithinFiveMinutes_DoesNotCancel()
    {
        using var dbContext = EmptyDbContext();
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult { Success = true, OrderId = "order-1", ClientOrderId = request.ClientOrderId, Status = "accepted" }),
            OrderByClientOrderIdResult = (_, _) => Task.FromResult(new AlpacaOrderResult { Success = true, OrderId = "order-1", Status = "filled" })
        };
        var service = CreateService(client, dbContext);

        var result = await service.FireCloseAsync(BuildPosition(entryQuantity: 0.3m), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        Assert.Contains("GetOrderByClientOrderIdAsync", client.CalledMethods);
        Assert.DoesNotContain("CancelOrderAsync", client.CalledMethods);
    }

    [Fact]
    public async Task FireCloseAsync_SubOneShareOrder_UnfilledAfterFiveMinutes_CancelsTheOrder()
    {
        using var dbContext = EmptyDbContext();
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

        var result = await service.FireCloseAsync(BuildPosition(entryQuantity: 0.3m), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        Assert.Contains("CancelOrderAsync", client.CalledMethods);
        Assert.Equal("order-1", canceledOrderId);
    }

    // --- Retry classification: terminal 4xx -------------------------------------------------

    [Fact]
    public async Task FireCloseAsync_TerminalRejection_DoesNotRetryOrCheckStatus()
    {
        using var dbContext = EmptyDbContext();
        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (_, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = false, IsAmbiguousFailure = false, ErrorMessage = "insufficient shares"
            })
        };
        var service = CreateService(client, dbContext);

        var result = await service.FireCloseAsync(BuildPosition(), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.RejectedByBroker, result.Outcome);
        Assert.Equal("insufficient shares", result.Reason);
        Assert.Equal(1, client.CalledMethods.Count(m => m == "PlaceOrderAsync"));
        Assert.DoesNotContain("GetOrderByClientOrderIdAsync", client.CalledMethods);
    }

    // --- Retry classification: ambiguous, found via status check ---------------------------

    [Fact]
    public async Task FireCloseAsync_AmbiguousFailureFoundViaStatusCheck_TreatedAsFiredWithoutRetrying()
    {
        using var dbContext = EmptyDbContext();
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

        var result = await service.FireCloseAsync(BuildPosition(), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        Assert.Equal("order-found", result.OrderId);
        Assert.Equal(1, client.CalledMethods.Count(m => m == "PlaceOrderAsync"));
        Assert.Equal(1, client.CalledMethods.Count(m => m == "GetOrderByClientOrderIdAsync"));
    }

    // --- Retry classification: ambiguous, not found, retry succeeds ------------------------

    [Fact]
    public async Task FireCloseAsync_AmbiguousFailureNotFound_RetriesOnceWithSameClientOrderIdAndSucceeds()
    {
        using var dbContext = EmptyDbContext();
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

        var result = await service.FireCloseAsync(BuildPosition(id: 3), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        Assert.Equal("order-retry", result.OrderId);
        Assert.Equal(2, placeCallCount);
        Assert.Equal(1, client.CalledMethods.Count(m => m == "GetOrderByClientOrderIdAsync"));
        Assert.Equal("drps-close-3-1", result.ClientOrderId);
    }

    // --- Retry classification: never retries more than once --------------------------------

    [Fact]
    public async Task FireCloseAsync_AmbiguousFailureTwice_ReturnsAmbiguousUnresolvedWithoutASecondRetry()
    {
        using var dbContext = EmptyDbContext();
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

        var result = await service.FireCloseAsync(BuildPosition(), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.AmbiguousUnresolved, result.Outcome);
        Assert.Equal(2, placeCallCount);
        Assert.Equal(1, client.CalledMethods.Count(m => m == "GetOrderByClientOrderIdAsync"));

        // Close-side counterpart to the open-side assertion in OrderFiringServiceTests - same
        // fourth Pushover trigger, same content requirements.
        var notification = Assert.Single(pushover.SentMessages);
        Assert.Contains("CLOSE", notification);
        Assert.Contains("AAPL", notification);
        Assert.Contains("AMBIGUOUS", notification);
        Assert.Contains(result.ClientOrderId!, notification);
        Assert.Contains("timeout", notification);
        Assert.Contains("AmbiguousUnresolved", notification);

        // CLAUDE.md's 2026-07-31 audit, Gap 1 - the close side writes a row too (a complete
        // audit trail), even though nothing reads/consumes it today - see AmbiguousFireSkip's
        // own doc comment for why the double-fire window this guards only applies to opens.
        var skip = await dbContext.AmbiguousFireSkips.SingleAsync(s => s.Ticker == "AAPL");
        Assert.Null(skip.ConsumedAt);
    }

    // --- Sentiment multiplier is never called for a close (Sentiment Adjuster Multiplier
    // Decision, CLAUDE.md 2026-07-24) ------------------------------------------------------

    [Fact]
    public async Task FireCloseAsync_NeverCallsSentimentMultiplierService()
    {
        using var dbContext = EmptyDbContext();
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"meta":{"found":0,"returned":0,"limit":3,"page":1},"data":[]}""")
            };
        });
        var httpClient = new HttpClient(handler);
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var marketauxClient = new MarketauxSentimentClient(
            httpClientFactory, Options.Create(new MarketauxOptions { ApiKey = "test-key" }), NullLogger<MarketauxSentimentClient>.Instance);
        var sentimentService = new SentimentMultiplierService(
            marketauxClient, Options.Create(new SentimentMultiplierOptions()), NullLogger<SentimentMultiplierService>.Instance);

        var client = new FakeAlpacaTradingClient
        {
            QuoteResult = (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m }),
            PlaceOrderResult = (request, _) => Task.FromResult(new AlpacaOrderResult
            {
                Success = true, OrderId = "order-1", ClientOrderId = request.ClientOrderId, Status = "filled"
            })
        };
        var service = CreateService(client, dbContext, sentimentMultiplierService: sentimentService);

        var result = await service.FireCloseAsync(BuildPosition(), CancellationToken.None);

        Assert.Equal(OrderFiringOutcome.Fired, result.Outcome);
        Assert.Equal(0, callCount);
        Assert.Null(result.BaseQuantity);
        Assert.Null(result.SentimentMultiplier);
    }
}
