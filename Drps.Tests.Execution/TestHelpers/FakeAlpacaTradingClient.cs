using Drps.Execution.Alpaca;

namespace Drps.Tests.TestHelpers;

// Hand-rolled IAlpacaTradingClient fake, same convention as FakeTradingCalendarService - no
// Moq. Every method defaults to an unconditional success so a test only needs to override the
// specific delegate it's exercising. CalledMethods records invocation order, letting a test
// assert short-circuit behavior (e.g. "GetAccountAsync/GetAssetAsync were never called after a
// kill-switch rejection") without depending on log output.
public class FakeAlpacaTradingClient : IAlpacaTradingClient
{
    public readonly List<string> CalledMethods = new();

    public Func<CancellationToken, Task<AlpacaAccountResult>> AccountResult { get; set; } =
        _ => Task.FromResult(new AlpacaAccountResult { Success = true, BuyingPower = 50000m, Cash = 50000m });

    public Func<CancellationToken, Task<AlpacaClockResult>> ClockResult { get; set; } =
        _ => Task.FromResult(new AlpacaClockResult
        {
            Success = true,
            IsOpen = true,
            NextOpen = DateTimeOffset.UtcNow.AddDays(1),
            NextClose = DateTimeOffset.UtcNow.AddHours(6)
        });

    public Func<string, CancellationToken, Task<AlpacaAssetResult>> AssetResult { get; set; } =
        (symbol, _) => Task.FromResult(new AlpacaAssetResult { Success = true, Symbol = symbol, Tradable = true, Status = "active" });

    public Func<CancellationToken, Task<AlpacaPositionsResult>> PositionsResult { get; set; } =
        _ => Task.FromResult(new AlpacaPositionsResult { Success = true });

    public Func<string, CancellationToken, Task<AlpacaOrdersResult>> OrdersBySymbolResult { get; set; } =
        (_, _) => Task.FromResult(new AlpacaOrdersResult { Success = true });

    public Func<AlpacaOrderRequest, CancellationToken, Task<AlpacaOrderResult>> PlaceOrderResult { get; set; } =
        (_, _) => throw new NotImplementedException("Override in tests that exercise order placement (e.g. OrderFiringServiceTests)");

    public Func<string, CancellationToken, Task<AlpacaOrderResult>> OrderByClientOrderIdResult { get; set; } =
        (_, _) => throw new NotImplementedException("Override in tests that exercise order-status lookup (e.g. OrderFiringServiceTests)");

    public Func<string, CancellationToken, Task<AlpacaQuoteResult>> QuoteResult { get; set; } =
        (_, _) => Task.FromResult(new AlpacaQuoteResult { Success = true, Ask = 100m, Bid = 99.9m });

    public Func<string, CancellationToken, Task<AlpacaCancelOrderResult>> CancelOrderResult { get; set; } =
        (_, _) => Task.FromResult(new AlpacaCancelOrderResult { Success = true });

    public Task<AlpacaAccountResult> GetAccountAsync(CancellationToken cancellationToken)
    {
        CalledMethods.Add(nameof(GetAccountAsync));
        return AccountResult(cancellationToken);
    }

    public Task<AlpacaClockResult> GetClockAsync(CancellationToken cancellationToken)
    {
        CalledMethods.Add(nameof(GetClockAsync));
        return ClockResult(cancellationToken);
    }

    public Task<AlpacaAssetResult> GetAssetAsync(string symbol, CancellationToken cancellationToken)
    {
        CalledMethods.Add(nameof(GetAssetAsync));
        return AssetResult(symbol, cancellationToken);
    }

    public Task<AlpacaPositionsResult> GetOpenPositionsAsync(CancellationToken cancellationToken)
    {
        CalledMethods.Add(nameof(GetOpenPositionsAsync));
        return PositionsResult(cancellationToken);
    }

    public Task<AlpacaOrdersResult> ListOrdersBySymbolAsync(string symbol, CancellationToken cancellationToken)
    {
        CalledMethods.Add(nameof(ListOrdersBySymbolAsync));
        return OrdersBySymbolResult(symbol, cancellationToken);
    }

    public Task<AlpacaOrderResult> PlaceOrderAsync(AlpacaOrderRequest request, CancellationToken cancellationToken)
    {
        CalledMethods.Add(nameof(PlaceOrderAsync));
        return PlaceOrderResult(request, cancellationToken);
    }

    public Task<AlpacaOrderResult> GetOrderByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken)
    {
        CalledMethods.Add(nameof(GetOrderByClientOrderIdAsync));
        return OrderByClientOrderIdResult(clientOrderId, cancellationToken);
    }

    public Task<AlpacaQuoteResult> GetLatestQuoteAsync(string symbol, CancellationToken cancellationToken)
    {
        CalledMethods.Add(nameof(GetLatestQuoteAsync));
        return QuoteResult(symbol, cancellationToken);
    }

    public Task<AlpacaCancelOrderResult> CancelOrderAsync(string orderId, CancellationToken cancellationToken)
    {
        CalledMethods.Add(nameof(CancelOrderAsync));
        return CancelOrderResult(orderId, cancellationToken);
    }
}
