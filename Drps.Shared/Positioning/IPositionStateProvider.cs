namespace Drps.Shared.Positioning;

/// <summary>
/// Seam for querying a ticker's current position state (held vs. not, and whether it's
/// currently NoBuy-blocked) - the real implementation belongs to Drps.Ledger once it exists
/// (Gate reads Ledger data live for exactly this kind of check, per CLAUDE.md's
/// Gate/Ledger/Monolith role clarification). This interface is only the seam; it is not a
/// Ledger implementation itself.
/// </summary>
public interface IPositionStateProvider
{
    bool IsCurrentlyHeld(string ticker);

    DateTime? GetNoBuyExpiration(string ticker);
}
