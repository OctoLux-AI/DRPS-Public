namespace Drps.Execution.Alpaca;

// Raw client surface over Alpaca's trading API (paper base URL) - account, clock, assets,
// positions, orders. Deliberately no gate/decision logic anywhere behind this interface;
// every consumer named in CLAUDE.md's Execution Layer design blocks (PreFireGateService's
// cash-floor/market-hours/asset-tradable checks, Reconciliation's position diff, the future
// fire mechanism) is a separate, not-yet-built task that will call these methods directly.
public interface IAlpacaTradingClient
{
    // Live buying power / cash balance - the source the future cash-floor pre-fire gate
    // queries directly (never DRPS's own computed TotalDeployedCapital).
    Task<AlpacaAccountResult> GetAccountAsync(CancellationToken cancellationToken);

    // Alpaca's own market clock/calendar - the source the future market-hours pre-fire gate
    // queries directly, rather than DRPS reimplementing NYSE calendar logic.
    Task<AlpacaClockResult> GetClockAsync(CancellationToken cancellationToken);

    // Tradable/status for a single symbol. Deliberately never cached by this client or any
    // caller - a halt is a real-time event, unlike the 7-day-TTL sector/ex-div reference
    // data elsewhere in this codebase. Throws SymbolNotFoundException on a confirmed 404.
    Task<AlpacaAssetResult> GetAssetAsync(string symbol, CancellationToken cancellationToken);

    // Alpaca's full real open-position list - what the future Reconciliation worker diffs
    // against Drps.Ledger's believed state.
    Task<AlpacaPositionsResult> GetOpenPositionsAsync(CancellationToken cancellationToken);

    // Closed (filled/canceled/expired/rejected) order history for a single symbol, server-side
    // filtered by both symbol and status=closed - never fetches open orders or every symbol's
    // history. What the future phantom-position auto-heal task (CLAUDE.md's Execution Layer:
    // Fifth Design Decision) searches to confirm whether a real fill already closed a position
    // Ledger still believes is open, without requiring a pre-known client_order_id the way
    // GetOrderByClientOrderIdAsync does.
    Task<AlpacaOrdersResult> ListOrdersBySymbolAsync(string symbol, CancellationToken cancellationToken);

    // Raw order placement - no retry policy (per CLAUDE.md's Execution Layer: Second Design
    // Decision, an order call's ambiguous-on-timeout risk means blind retry is unsafe; the
    // future fire-mechanism task owns that classification logic, not this client).
    Task<AlpacaOrderResult> PlaceOrderAsync(AlpacaOrderRequest request, CancellationToken cancellationToken);

    // Order status lookup by client_order_id. A confirmed 404 returns NotFound = true rather
    // than throwing - the future fire mechanism treats "does an order with this ID exist yet"
    // as an expected branch, not an exceptional one.
    Task<AlpacaOrderResult> GetOrderByClientOrderIdAsync(string clientOrderId, CancellationToken cancellationToken);

    // Live bid/ask for a single symbol - what OrderFiringService uses to compute a
    // marketable-limit price at the moment of firing (ask + 0.25% for a buy), rather than
    // sizing against a stale scan-time Close. Throws SymbolNotFoundException on a confirmed
    // 404, same carve-out as GetAssetAsync.
    Task<AlpacaQuoteResult> GetLatestQuoteAsync(string symbol, CancellationToken cancellationToken);

    // Raw order cancellation by Alpaca order ID - what OrderFiringService's 5-minute
    // cancel-if-unfilled check calls on a sub-1-share Day order that hasn't filled. No retry
    // policy, same reasoning as PlaceOrderAsync (a mutating call) - makes exactly one attempt.
    Task<AlpacaCancelOrderResult> CancelOrderAsync(string orderId, CancellationToken cancellationToken);
}
