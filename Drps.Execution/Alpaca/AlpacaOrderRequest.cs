namespace Drps.Execution.Alpaca;

// Raw request shape for POST /v2/orders. Every value here is a fully-computed input the
// caller supplies - this client does not decide the limit price, generate the
// client_order_id, or pick order type/time-in-force. Those decisions (marketable-limit
// pricing, the "drps-{GateScoreId}-{AdjusterAllocationId}-{attempt}" client_order_id scheme)
// belong to the future fire-mechanism task per CLAUDE.md's Execution Layer: Second Design
// Decision - this type only carries whatever that task ultimately computes.
public class AlpacaOrderRequest
{
    public required string Symbol { get; init; }
    public required decimal Quantity { get; init; }

    // "buy" or "sell".
    public required string Side { get; init; }

    // Alpaca order type, e.g. "limit", "market".
    public required string Type { get; init; }

    // Alpaca time-in-force code, e.g. "day", "gtc".
    public required string TimeInForce { get; init; }

    // Required when Type is "limit"; null for "market".
    public decimal? LimitPrice { get; init; }

    public required string ClientOrderId { get; init; }
}
