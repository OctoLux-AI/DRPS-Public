using System.Net;
using Drps.Execution.Alpaca;
using Drps.Ingestion;
using Drps.Shared.Exceptions;
using Drps.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Drps.Tests.Execution;

public class AlpacaTradingClientTests
{
    // Real shapes confirmed against Alpaca's own docs (docs.alpaca.markets) before writing the
    // client - every dollar-denominated field is a JSON string, never a bare number, same
    // convention AlpacaAccountFeeder already established; is_open/tradable are bare booleans.
    private const string AccountFixture = """
    {
        "id": "904837e3-3b76-47ec-b432-046db621571b",
        "status": "ACTIVE",
        "currency": "USD",
        "buying_power": "262113.632",
        "cash": "50000.12",
        "equity": "103820.56"
    }
    """;

    private const string ClockFixture = """
    {
        "timestamp": "2026-07-20T12:00:00.000Z",
        "is_open": true,
        "next_open": "2026-07-21T09:30:00.000Z",
        "next_close": "2026-07-20T16:00:00.000Z"
    }
    """;

    private const string AssetFixture = """
    {
        "id": "b0b6dd9d-8b9b-48a9-ba46-b9d54906e415",
        "class": "us_equity",
        "exchange": "NASDAQ",
        "symbol": "AAPL",
        "status": "active",
        "tradable": true,
        "marginable": true,
        "shortable": true,
        "easy_to_borrow": true,
        "fractionable": true
    }
    """;

    private const string TwoPositionsFixture = """
    [
        {
            "asset_id": "904837e3-3b76-47ec-b432-046db621571b",
            "symbol": "AAPL",
            "exchange": "NASDAQ",
            "avg_entry_price": "150.00",
            "qty": "10",
            "side": "long",
            "market_value": "1550.00"
        },
        {
            "asset_id": "b0b6dd9d-8b9b-48a9-ba46-b9d54906e415",
            "symbol": "NVDA",
            "exchange": "NASDAQ",
            "avg_entry_price": "120.50",
            "qty": "5",
            "side": "long",
            "market_value": "610.00"
        }
    ]
    """;

    // Realistic closed-order-history shape (GET /v2/orders?symbols=...&status=closed) - a
    // filled sell (what phantom-position auto-heal would search for) alongside a canceled buy
    // with zero fill, confirming the nullable filled_qty/filled_avg_price/filled_at fields are
    // handled correctly for an order that never filled at all.
    private const string ClosedOrdersFixture = """
    [
        {
            "id": "61e69015-8549-4bfd-b9c3-01e75843f47d",
            "client_order_id": "drps-close-42-1",
            "symbol": "AAPL",
            "asset_class": "us_equity",
            "qty": "5",
            "filled_qty": "5",
            "filled_avg_price": "210.50",
            "status": "filled",
            "side": "sell",
            "type": "limit",
            "time_in_force": "ioc",
            "filled_at": "2026-07-18T14:32:10.123Z",
            "created_at": "2026-07-18T14:32:00.000Z"
        },
        {
            "id": "72f7a026-9650-5cfe-c0d4-12f86954a58e",
            "client_order_id": "drps-2-1-1",
            "symbol": "AAPL",
            "asset_class": "us_equity",
            "qty": "10",
            "filled_qty": "0",
            "filled_avg_price": null,
            "status": "canceled",
            "side": "buy",
            "type": "limit",
            "time_in_force": "day",
            "filled_at": null,
            "canceled_at": "2026-07-17T15:00:00.000Z"
        }
    ]
    """;

    private const string AcceptedOrderFixture = """
    {
        "id": "61e69015-8549-4bfd-b9c3-01e75843f47d",
        "client_order_id": "drps-2-1-1",
        "symbol": "AAPL",
        "qty": "10",
        "filled_qty": "0",
        "filled_avg_price": null,
        "status": "accepted",
        "side": "buy",
        "type": "limit",
        "time_in_force": "day"
    }
    """;

    private const string FilledOrderFixture = """
    {
        "id": "61e69015-8549-4bfd-b9c3-01e75843f47d",
        "client_order_id": "drps-2-1-1",
        "symbol": "AAPL",
        "qty": "10",
        "filled_qty": "10",
        "filled_avg_price": "150.30",
        "status": "filled",
        "side": "buy",
        "type": "limit",
        "time_in_force": "day"
    }
    """;

    // Market-data API shape (data.alpaca.markets) - ap/bp are bare JSON numbers, unlike the
    // trading API's quoted-string dollar fields above (AccountFixture etc.).
    private const string QuoteFixture = """
    {
        "symbol": "AAPL",
        "quote": {
            "t": "2026-07-20T13:01:57.822769Z",
            "ax": "Q",
            "ap": 133.55,
            "as": 7,
            "bx": "K",
            "bp": 133.50,
            "bs": 1
        }
    }
    """;

    private static AlpacaTradingClient CreateClient(FakeHttpMessageHandler handler, AlpacaOptions? options = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://paper-api.alpaca.markets") };
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var resolvedOptions = Options.Create(options ?? new AlpacaOptions { KeyId = "test-key-id", SecretKey = "test-secret" });
        return new AlpacaTradingClient(httpClientFactory, resolvedOptions, NullLogger<AlpacaTradingClient>.Instance);
    }

    // --- GetAccountAsync -----------------------------------------------------------------

    [Fact]
    public async Task GetAccountAsync_SuccessfulResponse_ParsesBuyingPowerAndCash()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AccountFixture) });
        var client = CreateClient(handler);

        var result = await client.GetAccountAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(262113.632m, result.BuyingPower);
        Assert.Equal(50000.12m, result.Cash);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task GetAccountAsync_Always_RequestsAccountEndpoint()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AccountFixture) });
        var client = CreateClient(handler);

        await client.GetAccountAsync(CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/v2/account", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetAccountAsync_MissingCashField_ReturnsFailureWithoutThrowing()
    {
        const string missingFieldFixture = """{"buying_power": "1000.00"}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(missingFieldFixture) });
        var client = CreateClient(handler);

        var result = await client.GetAccountAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.BuyingPower);
        Assert.Null(result.Cash);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public async Task GetAccountAsync_NonRetryableClientError_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"forbidden"}""") });
        var client = CreateClient(handler);

        var result = await client.GetAccountAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    // This test takes several real seconds: the shared FeederRetryPolicy's exponential
    // backoff (2s, 4s, 8s) actually elapses, same precedent as AlpacaFeeder/AlpacaAccountFeeder's
    // own retry-exhaustion tests - confirms the read-only GET methods really are wired to the
    // shared policy, not just calling client.GetAsync directly.
    [Fact]
    public async Task GetAccountAsync_PersistentTransientFailure_RetriesThreeTimesThenReturnsFailure()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });
        var client = CreateClient(handler);

        var result = await client.GetAccountAsync(CancellationToken.None);

        Assert.Equal(4, callCount);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task GetAccountAsync_MissingKeyId_ThrowsConfigurationMissingExceptionWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP call should never have been attempted"));
        var client = CreateClient(handler, new AlpacaOptions { KeyId = "", SecretKey = "test-secret" });

        var exception = await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            client.GetAccountAsync(CancellationToken.None));

        Assert.Equal("Alpaca:KeyId", exception.ConfigKey);
        Assert.Null(handler.LastRequest);
    }

    // --- GetClockAsync ---------------------------------------------------------------------

    [Fact]
    public async Task GetClockAsync_SuccessfulResponse_ParsesIsOpenAndTimes()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClockFixture) });
        var client = CreateClient(handler);

        var result = await client.GetClockAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.IsOpen);
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 9, 30, 0, TimeSpan.Zero), result.NextOpen);
        Assert.Equal(new DateTimeOffset(2026, 7, 20, 16, 0, 0, TimeSpan.Zero), result.NextClose);
    }

    [Fact]
    public async Task GetClockAsync_Always_RequestsClockEndpoint()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClockFixture) });
        var client = CreateClient(handler);

        await client.GetClockAsync(CancellationToken.None);

        Assert.Equal("/v2/clock", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetClockAsync_MissingIsOpenField_ReturnsFailureWithoutThrowing()
    {
        const string missingFieldFixture = """{"next_open": "2026-07-21T09:30:00.000Z"}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(missingFieldFixture) });
        var client = CreateClient(handler);

        var result = await client.GetClockAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.IsOpen);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    // --- GetAssetAsync -----------------------------------------------------------------------

    [Fact]
    public async Task GetAssetAsync_SuccessfulResponse_ParsesTradableAndStatus()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AssetFixture) });
        var client = CreateClient(handler);

        var result = await client.GetAssetAsync("AAPL", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("AAPL", result.Symbol);
        Assert.True(result.Tradable);
        Assert.Equal("active", result.Status);
    }

    [Fact]
    public async Task GetAssetAsync_Always_RequestsAssetEndpointForSymbol()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AssetFixture) });
        var client = CreateClient(handler);

        await client.GetAssetAsync("AAPL", CancellationToken.None);

        Assert.Equal("/v2/assets/AAPL", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetAssetAsync_UnknownSymbol_ThrowsSymbolNotFoundExceptionWithoutRetrying()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<SymbolNotFoundException>(() =>
            client.GetAssetAsync("ZZZZINVALID", CancellationToken.None));

        Assert.Equal("ZZZZINVALID", exception.Symbol);
        // 404 is not a transient status per FeederRetryPolicy.IsTransient - exactly one call.
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetAssetAsync_MissingTradableField_ReturnsFailureWithoutThrowing()
    {
        const string missingFieldFixture = """{"symbol": "AAPL", "status": "active"}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(missingFieldFixture) });
        var client = CreateClient(handler);

        var result = await client.GetAssetAsync("AAPL", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Tradable);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    // --- GetOpenPositionsAsync -----------------------------------------------------------------

    [Fact]
    public async Task GetOpenPositionsAsync_SuccessfulResponse_MapsAllPositions()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(TwoPositionsFixture) });
        var client = CreateClient(handler);

        var result = await client.GetOpenPositionsAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Positions.Count);

        var first = result.Positions[0];
        Assert.Equal("AAPL", first.Symbol);
        Assert.Equal(10m, first.Quantity);
        Assert.Equal(150.00m, first.AverageEntryPrice);
        Assert.Equal(1550.00m, first.MarketValue);
        Assert.Equal("long", first.Side);

        Assert.Equal("NVDA", result.Positions[1].Symbol);
    }

    [Fact]
    public async Task GetOpenPositionsAsync_EmptyAccount_ReturnsSuccessWithZeroPositions()
    {
        // A genuinely empty account returns 200 + "[]" - a real, valid outcome, not a
        // failure - same "expected empty result is Success, not an error" precedent as
        // AlpacaFeeder's NoDataForRange handling.
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var client = CreateClient(handler);

        var result = await client.GetOpenPositionsAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Positions);
    }

    [Fact]
    public async Task GetOpenPositionsAsync_NonArrayResponse_ReturnsFailureWithoutThrowing()
    {
        const string nonArrayFixture = """{"message": "unexpected shape"}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(nonArrayFixture) });
        var client = CreateClient(handler);

        var result = await client.GetOpenPositionsAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(result.Positions);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    // --- ListOrdersBySymbolAsync -----------------------------------------------------------

    [Fact]
    public async Task ListOrdersBySymbolAsync_SuccessfulResponse_MapsAllOrders()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClosedOrdersFixture) });
        var client = CreateClient(handler);

        var result = await client.ListOrdersBySymbolAsync("AAPL", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Orders.Count);

        var filled = result.Orders[0];
        Assert.Equal("61e69015-8549-4bfd-b9c3-01e75843f47d", filled.OrderId);
        Assert.Equal("drps-close-42-1", filled.ClientOrderId);
        Assert.Equal("AAPL", filled.Symbol);
        Assert.Equal("sell", filled.Side);
        Assert.Equal("filled", filled.Status);
        Assert.Equal(5m, filled.FilledQuantity);
        Assert.Equal(210.50m, filled.FilledAveragePrice);
        Assert.Equal(new DateTimeOffset(2026, 7, 18, 14, 32, 10, 123, TimeSpan.Zero), filled.FilledAt);

        var canceled = result.Orders[1];
        Assert.Equal("canceled", canceled.Status);
        Assert.Equal("buy", canceled.Side);
        Assert.Equal(0m, canceled.FilledQuantity);
        Assert.Null(canceled.FilledAveragePrice);
        Assert.Null(canceled.FilledAt);
    }

    [Fact]
    public async Task ListOrdersBySymbolAsync_Always_RequestsOrdersEndpointFilteredBySymbolAndClosedStatus()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ClosedOrdersFixture) });
        var client = CreateClient(handler);

        await client.ListOrdersBySymbolAsync("AAPL", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/v2/orders", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("symbols=AAPL", handler.LastRequest.RequestUri.Query);
        Assert.Contains("status=closed", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task ListOrdersBySymbolAsync_NoClosedOrdersForSymbol_ReturnsSuccessWithEmptyList()
    {
        // A symbol with no closed order history yet returns 200 + "[]" - a real, valid outcome,
        // same "expected empty result is Success, not an error" precedent as
        // GetOpenPositionsAsync's own empty-account test.
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var client = CreateClient(handler);

        var result = await client.ListOrdersBySymbolAsync("ZZZZINVALID", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Orders);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task ListOrdersBySymbolAsync_NonRetryableClientError_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"message":"forbidden"}""") });
        var client = CreateClient(handler);

        var result = await client.ListOrdersBySymbolAsync("AAPL", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(result.Orders);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public async Task ListOrdersBySymbolAsync_NonArrayResponse_ReturnsFailureWithoutThrowing()
    {
        const string nonArrayFixture = """{"message": "unexpected shape"}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(nonArrayFixture) });
        var client = CreateClient(handler);

        var result = await client.ListOrdersBySymbolAsync("AAPL", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(result.Orders);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public async Task ListOrdersBySymbolAsync_MissingKeyId_ThrowsConfigurationMissingExceptionWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP call should never have been attempted"));
        var client = CreateClient(handler, new AlpacaOptions { KeyId = "", SecretKey = "test-secret" });

        var exception = await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            client.ListOrdersBySymbolAsync("AAPL", CancellationToken.None));

        Assert.Equal("Alpaca:KeyId", exception.ConfigKey);
        Assert.Null(handler.LastRequest);
    }

    // --- PlaceOrderAsync -------------------------------------------------------------------

    private static AlpacaOrderRequest BuildOrderRequest() => new()
    {
        Symbol = "AAPL",
        Quantity = 10m,
        Side = "buy",
        Type = "limit",
        TimeInForce = "day",
        LimitPrice = 150.25m,
        ClientOrderId = "drps-2-1-1"
    };

    [Fact]
    public async Task PlaceOrderAsync_SuccessfulResponse_ParsesOrderFields()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AcceptedOrderFixture) });
        var client = CreateClient(handler);

        var result = await client.PlaceOrderAsync(BuildOrderRequest(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.NotFound);
        Assert.Equal("61e69015-8549-4bfd-b9c3-01e75843f47d", result.OrderId);
        Assert.Equal("drps-2-1-1", result.ClientOrderId);
        Assert.Equal("accepted", result.Status);
        Assert.Equal(0m, result.FilledQuantity);
        Assert.Null(result.FilledAveragePrice);
    }

    [Fact]
    public async Task PlaceOrderAsync_Always_PostsToOrdersEndpoint()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(AcceptedOrderFixture) });
        var client = CreateClient(handler);

        await client.PlaceOrderAsync(BuildOrderRequest(), CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("/v2/orders", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task PlaceOrderAsync_RejectedOrder_ReturnsFailureWithBrokerMessage()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"code":40310000,"message":"insufficient buying power"}""")
            });
        var client = CreateClient(handler);

        var result = await client.PlaceOrderAsync(BuildOrderRequest(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("insufficient buying power", result.ErrorMessage);
    }

    // Locks in the deliberate design decision (Execution Layer: Second Design Decision) that
    // order placement is never blindly retried - a timeout/5xx here is ambiguous (the order
    // may have already fired), so PlaceOrderAsync makes exactly one attempt, unlike every
    // GET method above.
    [Fact]
    public async Task PlaceOrderAsync_TransientServerError_DoesNotRetry()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var client = CreateClient(handler);

        var result = await client.PlaceOrderAsync(BuildOrderRequest(), CancellationToken.None);

        Assert.Equal(1, callCount);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task PlaceOrderAsync_CleanRejection_IsNotFlaggedAmbiguous()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("""{"code":40310000,"message":"insufficient buying power"}""")
            });
        var client = CreateClient(handler);

        var result = await client.PlaceOrderAsync(BuildOrderRequest(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.IsAmbiguousFailure);
    }

    [Fact]
    public async Task PlaceOrderAsync_ServerError_IsFlaggedAmbiguous()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var client = CreateClient(handler);

        var result = await client.PlaceOrderAsync(BuildOrderRequest(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.IsAmbiguousFailure);
    }

    [Fact]
    public async Task PlaceOrderAsync_NetworkException_IsFlaggedAmbiguous()
    {
        // No confirmed HTTP response was ever received - inherently ambiguous, same category
        // as a 5xx above (OrderFiringService's retry classification treats both identically).
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection reset"));
        var client = CreateClient(handler);

        var result = await client.PlaceOrderAsync(BuildOrderRequest(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.IsAmbiguousFailure);
    }

    [Fact]
    public async Task PlaceOrderAsync_MissingSecretKey_ThrowsConfigurationMissingExceptionWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP call should never have been attempted"));
        var client = CreateClient(handler, new AlpacaOptions { KeyId = "test-key-id", SecretKey = "   " });

        var exception = await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            client.PlaceOrderAsync(BuildOrderRequest(), CancellationToken.None));

        Assert.Equal("Alpaca:SecretKey", exception.ConfigKey);
        Assert.Null(handler.LastRequest);
    }

    // --- GetOrderByClientOrderIdAsync -------------------------------------------------------

    [Fact]
    public async Task GetOrderByClientOrderIdAsync_SuccessfulResponse_ParsesFilledOrder()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FilledOrderFixture) });
        var client = CreateClient(handler);

        var result = await client.GetOrderByClientOrderIdAsync("drps-2-1-1", CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.NotFound);
        Assert.Equal("filled", result.Status);
        Assert.Equal(10m, result.FilledQuantity);
        Assert.Equal(150.30m, result.FilledAveragePrice);
    }

    [Fact]
    public async Task GetOrderByClientOrderIdAsync_Always_RequestsByClientOrderIdEndpoint()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FilledOrderFixture) });
        var client = CreateClient(handler);

        await client.GetOrderByClientOrderIdAsync("drps-2-1-1", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/v2/orders:by_client_order_id", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("client_order_id=drps-2-1-1", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task GetOrderByClientOrderIdAsync_UnknownOrder_ReturnsNotFoundWithoutThrowing()
    {
        // Distinct from GetAssetAsync's SymbolNotFoundException carve-out: the future fire
        // mechanism's ambiguous-timeout retry logic treats "does an order with this ID exist
        // yet" as expected control flow, not an exceptional case - so this must return data,
        // never throw.
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = CreateClient(handler);

        var result = await client.GetOrderByClientOrderIdAsync("drps-does-not-exist", CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.NotFound);
        Assert.Null(result.ErrorMessage);
    }

    // --- GetLatestQuoteAsync -----------------------------------------------------------------

    [Fact]
    public async Task GetLatestQuoteAsync_SuccessfulResponse_ParsesBidAndAsk()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(QuoteFixture) });
        var client = CreateClient(handler);

        var result = await client.GetLatestQuoteAsync("AAPL", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(133.55m, result.Ask);
        Assert.Equal(133.50m, result.Bid);
    }

    [Fact]
    public async Task GetLatestQuoteAsync_Always_RequestsDataApiEndpointWithIexFeed()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(QuoteFixture) });
        var client = CreateClient(handler);

        await client.GetLatestQuoteAsync("AAPL", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        // Built as a full absolute URI against DataBaseUrl, distinct from every other method's
        // trading-API BaseAddress (paper-api.alpaca.markets, per CreateClient above).
        Assert.Equal("data.alpaca.markets", handler.LastRequest!.RequestUri!.Host);
        Assert.Equal("/v2/stocks/AAPL/quotes/latest", handler.LastRequest.RequestUri.AbsolutePath);
        Assert.Contains("feed=iex", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task GetLatestQuoteAsync_UnknownSymbol_ThrowsSymbolNotFoundExceptionWithoutRetrying()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<SymbolNotFoundException>(() =>
            client.GetLatestQuoteAsync("ZZZZINVALID", CancellationToken.None));

        Assert.Equal("ZZZZINVALID", exception.Symbol);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task GetLatestQuoteAsync_MissingQuoteField_ReturnsFailureWithoutThrowing()
    {
        const string missingFieldFixture = """{"symbol": "AAPL"}""";
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(missingFieldFixture) });
        var client = CreateClient(handler);

        var result = await client.GetLatestQuoteAsync("AAPL", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(result.Ask);
        Assert.Null(result.Bid);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
    }

    [Fact]
    public async Task GetLatestQuoteAsync_MissingKeyId_ThrowsConfigurationMissingExceptionWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP call should never have been attempted"));
        var client = CreateClient(handler, new AlpacaOptions { KeyId = "", SecretKey = "test-secret" });

        var exception = await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            client.GetLatestQuoteAsync("AAPL", CancellationToken.None));

        Assert.Equal("Alpaca:KeyId", exception.ConfigKey);
        Assert.Null(handler.LastRequest);
    }

    // --- CancelOrderAsync --------------------------------------------------------------------

    [Fact]
    public async Task CancelOrderAsync_SuccessfulResponse_ReturnsSuccess()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(handler);

        var result = await client.CancelOrderAsync("61e69015-8549-4bfd-b9c3-01e75843f47d", CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task CancelOrderAsync_Always_SendsDeleteToOrderEndpoint()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = CreateClient(handler);

        await client.CancelOrderAsync("61e69015-8549-4bfd-b9c3-01e75843f47d", CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
        Assert.Equal("/v2/orders/61e69015-8549-4bfd-b9c3-01e75843f47d", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task CancelOrderAsync_OrderAlreadyFilled_ReturnsFailureWithoutThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
            {
                Content = new StringContent("""{"message":"order already filled"}""")
            });
        var client = CreateClient(handler);

        var result = await client.CancelOrderAsync("61e69015-8549-4bfd-b9c3-01e75843f47d", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("order already filled", result.ErrorMessage);
    }

    // Locks in the same deliberate no-retry design as PlaceOrderAsync - a mutating call, one
    // attempt only.
    [Fact]
    public async Task CancelOrderAsync_TransientServerError_DoesNotRetry()
    {
        var callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            callCount++;
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var client = CreateClient(handler);

        var result = await client.CancelOrderAsync("61e69015-8549-4bfd-b9c3-01e75843f47d", CancellationToken.None);

        Assert.Equal(1, callCount);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task CancelOrderAsync_MissingKeyId_ThrowsConfigurationMissingExceptionWithoutSendingRequest()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("HTTP call should never have been attempted"));
        var client = CreateClient(handler, new AlpacaOptions { KeyId = "", SecretKey = "test-secret" });

        var exception = await Assert.ThrowsAsync<ConfigurationMissingException>(() =>
            client.CancelOrderAsync("61e69015-8549-4bfd-b9c3-01e75843f47d", CancellationToken.None));

        Assert.Equal("Alpaca:KeyId", exception.ConfigKey);
        Assert.Null(handler.LastRequest);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }
}
