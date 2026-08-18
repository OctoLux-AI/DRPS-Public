using Drps.Shared.Positioning;

namespace Drps.Gate.Positioning;

/// <summary>
/// Placeholder implementation of IPositionStateProvider until Drps.Ledger exists to answer
/// these questions for real. Always reports "not held, no NoBuy block" for every ticker -
/// correct today, since Drps.Ledger has never recorded a real position (order execution is
/// still unbuilt, per CLAUDE.md's parked "denied open/close orders" item). Replace this
/// registration with a real Drps.Ledger-backed implementation once that project exists.
/// </summary>
public class StubPositionStateProvider : IPositionStateProvider
{
    public bool IsCurrentlyHeld(string ticker) => false;

    public DateTime? GetNoBuyExpiration(string ticker) => null;
}
