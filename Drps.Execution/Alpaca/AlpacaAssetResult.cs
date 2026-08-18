namespace Drps.Execution.Alpaca;

// A 404 (Alpaca confirms no asset exists for the requested symbol) is not represented here —
// it throws SymbolNotFoundException instead, same carve-out precedent as TiingoFeeder's 404
// handling, since "no asset exists" is a distinct outcome from "the call failed".
public class AlpacaAssetResult
{
    public bool Success { get; set; }

    // All null whenever Success is false.
    public string? Symbol { get; set; }
    public bool? Tradable { get; set; }
    public string? Status { get; set; }

    public string? ErrorMessage { get; set; }
}
