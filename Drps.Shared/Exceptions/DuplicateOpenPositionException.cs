namespace Drps.Shared.Exceptions;

// Thrown by LedgerPositionWriter.OpenPositionAsync when the database's own filtered unique
// index (Ticker, WHERE ExitDate IS NULL) rejects an insert - translated from EF Core's raw
// DbUpdateException/unique-constraint violation into a specific, Ledger-meaningful exception.
// Explicitly a belt-and-suspenders check: OpenCandidateQuery's own idempotency logic already
// prevents this in the normal path (CLAUDE.md's Execution Layer: Sixth Design Decision) - this
// is the database itself refusing even if that upstream check is ever bypassed or buggy.
public class DuplicateOpenPositionException : Exception
{
    public string Ticker { get; }

    public DuplicateOpenPositionException(string ticker, Exception innerException)
        : base($"Ticker '{ticker}' already has an open position - refusing to open a second one.", innerException)
    {
        Ticker = ticker;
    }
}
