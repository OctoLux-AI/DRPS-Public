using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ingestion.Persistence.Seeding;

/// <summary>
/// One-time seed for GateParameters' first real row - human-authored configuration data,
/// not externally-fetched market data, so this is deliberately not a feeder or a worker and
/// is never invoked automatically on startup. The only call site is Program.cs's opt-in
/// "--seed-gate-parameters" command-line flag, run deliberately by a human, not on every
/// application start.
///
/// Values are written out explicitly here rather than relying on the entity's own
/// initializers, so this seed's intent stays legible even if a future GateParameters property
/// default changes for an unrelated reason.
///
/// [REDACTED FOR PUBLIC RELEASE] The specific values below are placeholders matching
/// GateParameters.cs's own redacted defaults, not DRPS's real shipped tuning - see README.md's
/// "What's intentionally not public" section.
/// </summary>
public static class GateParametersSeeder
{
    // `effectiveFrom` is supplied by the caller, never read from an internally-held clock -
    // matches Gate's existing clock-injection discipline (GateScanService's own doc comment)
    // even though this is a one-off script, not a recurring service.
    //
    // Fail-closed against a duplicate active row - thrown, not silently skipped, so a human
    // running this manually a second time by accident finds out immediately rather than the
    // mistake passing quietly. Seeding is a one-time human action, not idempotent startup
    // logic.
    public static async Task SeedAsync(DrpsDbContext dbContext, DateTime effectiveFrom, CancellationToken cancellationToken)
    {
        var activeRowExists = await dbContext.GateParameters.AnyAsync(p => p.IsActive, cancellationToken);
        if (activeRowExists)
        {
            throw new InvalidOperationException(
                "An active GateParameters row already exists - refusing to seed a second one. " +
                "This seed is a one-time human action, not idempotent startup logic.");
        }

        var parameters = new GateParameters
        {
            EffectiveFrom = effectiveFrom,
            IsActive = true,
            RsiLowerBound = 40m,
            RsiPeak = 50m,
            RsiUpperBound = 60m,
            RsiFloorQuality = 0.5m,
            RvolFloorMultiple = 0.5m,
            RvolCeilingMultiple = 1.5m,
            RvolFullWeight = 0.5m,
            RvolHalfWeight = 0.25m,
            RsiCompositeWeight = 0.5m,
            BuyThreshold = 0.9m,
            WatchThreshold = 0.6m,
            ExitThreshold = 0.3m,
            NoBuySessionCount = 2
        };

        dbContext.GateParameters.Add(parameters);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
