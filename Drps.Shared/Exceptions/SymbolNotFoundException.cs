namespace Drps.Shared.Exceptions;

// Thrown by a feeder when the source gives an explicit, confirmed "no data exists for
// this symbol" response (e.g. Tiingo's 404 on /tiingo/daily/{ticker}/prices for an
// unknown or delisted ticker). Distinct from a real fetch failure so orchestration can
// avoid counting bad INPUT (an invalid ticker) as a strike against the source's trust
// state — same carve-out precedent as ConfigurationMissingException, but for the
// opposite end of the request lifecycle: this fires after a real response was received
// and correctly interpreted, not before a request was ever sent.
public class SymbolNotFoundException : Exception
{
    public string Symbol { get; }

    public SymbolNotFoundException(string symbol)
        : base($"Source confirmed no data exists for symbol '{symbol}' (not a fetch failure).")
    {
        Symbol = symbol;
    }
}
