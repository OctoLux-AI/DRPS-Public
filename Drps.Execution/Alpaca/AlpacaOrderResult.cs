namespace Drps.Execution.Alpaca;

public class AlpacaOrderResult
{
    public bool Success { get; set; }

    // True only for GetOrderByClientOrderIdAsync's confirmed-404 case - a distinct, expected
    // outcome (the future fire-mechanism task's ambiguous-timeout retry logic branches on
    // this specifically, per CLAUDE.md's Execution Layer: Second Design Decision), not lumped
    // in with a generic Success = false failure. Always false for PlaceOrderAsync.
    public bool NotFound { get; set; }

    // Meaningful only when Success is false, and only for PlaceOrderAsync's own result - this
    // is what OrderFiringService's retry classification (CLAUDE.md's Execution Layer: Second
    // Design Decision) branches on. True for a timeout/5xx (no confirmed response was ever
    // received, or the server itself failed - Alpaca may have already processed the order);
    // false for a clean, definite 4xx rejection. Always false for every other method's own
    // failure paths - none of them need this distinction.
    public bool IsAmbiguousFailure { get; set; }

    // All null whenever Success is false.
    public string? OrderId { get; set; }
    public string? ClientOrderId { get; set; }
    public string? Status { get; set; }
    public decimal? FilledQuantity { get; set; }
    public decimal? FilledAveragePrice { get; set; }

    public string? ErrorMessage { get; set; }
}
