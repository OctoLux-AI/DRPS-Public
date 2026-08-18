namespace Drps.Execution.Alpaca;

// One entry from GET /v2/orders?symbols={symbol}&status=closed - the terminal-state order
// history ListOrdersBySymbolAsync searches, for the future phantom-position auto-heal task
// (CLAUDE.md's Execution Layer: Fifth Design Decision) to confirm whether a real fill already
// closed a position Ledger still believes is open, without requiring a pre-known
// client_order_id. Deliberately narrow, same precedent as AlpacaPosition - only the fields that
// search actually needs, not every field Alpaca's order object carries.
public readonly record struct AlpacaOrder
{
    public required string OrderId { get; init; }
    public required string ClientOrderId { get; init; }
    public required string Symbol { get; init; }
    public required string Side { get; init; }
    public required string Status { get; init; }

    // Null whenever this order never filled at all (e.g. a canceled/expired/rejected order with
    // zero fill) - "closed" per Alpaca's own status filter covers all four terminal outcomes,
    // not just filled.
    public decimal? FilledQuantity { get; init; }
    public decimal? FilledAveragePrice { get; init; }
    public DateTimeOffset? FilledAt { get; init; }
}
