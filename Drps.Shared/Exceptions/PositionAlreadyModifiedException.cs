namespace Drps.Shared.Exceptions;

// Thrown by LedgerPositionWriter.ClosePositionAsync when EF Core's own
// DbUpdateConcurrencyException fires on the Position row's RowVersion concurrency token -
// translated into a specific, Ledger-meaningful exception rather than letting EF's generic
// exception type leak past this write path (same "explicit, named failure mode" precedent as
// ConfigurationMissingException/SymbolNotFoundException). Fires when two writers race to close
// the same Position: whichever SaveChangesAsync commits first wins; the other gets this instead
// of a raw EF exception.
public class PositionAlreadyModifiedException : Exception
{
    public long PositionId { get; }

    public PositionAlreadyModifiedException(long positionId, Exception innerException)
        : base(
            $"Position {positionId} was modified concurrently by another writer - refusing to close it against stale data.",
            innerException)
    {
        PositionId = positionId;
    }
}
