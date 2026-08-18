using Drps.Shared.Positioning;

namespace Drps.Adjuster.Positioning;

/// <summary>
/// Placeholder implementation of IPortfolioStateProvider until Drps.Ledger exists to answer
/// this for real. Always reports zero deployed capital and an empty per-sector breakdown -
/// correct today regardless of source, since DRPS has no execution layer yet (order
/// placement is still unbuilt, per CLAUDE.md's parked "denied open/close orders" item) - even
/// a live Alpaca positions-endpoint pull would just return empty. Replace this registration
/// with a real Drps.Ledger-backed implementation once that project exists, same precedent as
/// Drps.Gate's StubPositionStateProvider.
/// </summary>
public class StubPortfolioStateProvider : IPortfolioStateProvider
{
    public Task<PortfolioState> GetCurrentStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new PortfolioState(
            TotalDeployedCapital: 0m,
            DeployedCapitalBySector: new Dictionary<string, decimal>()));
}
