namespace Drps.Ingestion.Feeders;

// One individual transactionCode == "P" entry parsed out of a single Form 4 filing's XML.
// DollarValue is computed by the caller (Shares x PricePerShare) rather than carried here,
// keeping this a pure parse result with no derived math baked in.
public readonly record struct Form4PurchaseTransaction(DateOnly TransactionDate, decimal Shares, decimal PricePerShare, string? InsiderName);
