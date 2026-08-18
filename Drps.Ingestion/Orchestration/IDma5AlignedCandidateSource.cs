namespace Drps.Ingestion.Orchestration;

/// <summary>
/// Ticker source for EarningsWorker's daily sweep - CLAUDE.md's "FIX: Repoint EarningsWorker to
/// Calculator's dynamic DMA-5-aligned candidate list" (2026-08-08). Replaces the static
/// 40-symbol Watchlist that previously drove this Worker's per-ticker loop: earnings-blackout
/// verification now targets exactly the tickers Drps.Calculator's RollingDmaStateService
/// currently reports as DMA-5-aligned - the same discovery-scoping source CLAUDE.md's
/// "Universe-Wide Dynamic Discovery" decision (2026-08-05) already repointed Drps.Calculator's
/// own per-symbol compute loop onto (RollingDmaStateService.GetDma5AlignedTickersAsync).
///
/// Abstracted behind an interface - matching this codebase's IFredCsvTransport/IGitCommandRunner
/// precedent for a fakeable external-I/O boundary - so EarningsWorker's own tests never need a
/// real database connection, and so the real implementation (raw Microsoft.Data.SqlClient, see
/// SqlDma5AlignedCandidateSource) can read RollingDmaStates without Drps.Ingestion taking on a
/// ProjectReference to Drps.Calculator, which owns that table.
/// </summary>
public interface IDma5AlignedCandidateSource
{
    Task<IReadOnlyList<string>> GetDma5AlignedTickersAsync(CancellationToken cancellationToken);
}
