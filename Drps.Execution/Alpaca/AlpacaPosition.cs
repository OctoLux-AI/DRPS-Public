namespace Drps.Execution.Alpaca;

// One entry from GET /v2/positions. Deliberately narrow - only the fields Reconciliation's
// future Ledger-diff (Execution Layer: Fifth Design Decision) actually needs, not every field
// Alpaca's position object carries.
public readonly record struct AlpacaPosition
{
    public required string Symbol { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal AverageEntryPrice { get; init; }
    public required decimal MarketValue { get; init; }
    public required string Side { get; init; }
}
