using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ingestion.Persistence.Seeding;

/// <summary>
/// One-time seed retiring NVDA's stale GateScore Id=2 (ScanDate 7/16/2026 - seed/test data used
/// to jump-start the system, not a real trading signal) from OrchestrationWorker's open-candidate
/// consideration. Human-authored data, not externally-fetched market data, so this is deliberately
/// not a feeder or a worker and is never invoked automatically on startup - same shape as
/// GateParametersSeeder, whose only call site is Program.cs's own opt-in command-line flag.
///
/// Unlike GateParametersSeeder (which throws on a pre-existing active row, since a second active
/// GateParameters row would be a genuine data-integrity problem), this seed is idempotent: a
/// ticker either is or isn't excluded (ExcludedTicker.Ticker carries a unique index), so a second
/// run simply logs and skips rather than erroring - the exact same check-then-insert idiom
/// ReconciliationService.HandleOrphanAsync already uses for this same table's only other writer.
/// Deliberately NOT a generic "seed any ticker" tool - this is a single, hardcoded insert for one
/// specific known-stale row, not a feature for managing ExcludedTickers going forward.
/// </summary>
public static class ExcludedTickerSeeder
{
    public const string NvdaTicker = "NVDA";

    public const string NvdaReason =
        "Seed/test data used to jump-start the system, GateScore Id 2, ScanDate 7/16/2026 - not a real trading signal.";

    // `createdDate` is supplied by the caller, never read from an internally-held clock - matches
    // this codebase's existing clock-injection discipline even though this is a one-off script,
    // not a recurring service.
    public static async Task<bool> SeedNvdaAsync(DrpsDbContext dbContext, DateTime createdDate, CancellationToken cancellationToken)
    {
        var alreadyExcluded = await dbContext.ExcludedTickers
            .AnyAsync(e => e.Ticker == NvdaTicker, cancellationToken);

        if (alreadyExcluded)
        {
            return false;
        }

        dbContext.ExcludedTickers.Add(new ExcludedTicker
        {
            Ticker = NvdaTicker,
            Reason = NvdaReason,
            CreatedDate = createdDate
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
