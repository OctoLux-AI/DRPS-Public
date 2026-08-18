namespace Drps.Adjuster.OptionsFlow;

/// <summary>
/// Options-flow (put/call volume ratio) sizing-adjustment multiplier - pure formula only, no
/// live data source wired up yet. Upgrade-only by construction, same shape as
/// InsiderLookupService.ComputeMultiplier - there is no subtraction term and no path that
/// produces a result below 1.0.
///
/// Standalone - not called by AdjusterSizingService/AdjusterScanService,
/// MultiSignalMultiplierCombiner, PreFireGateService, or OrderFiringService yet, and no
/// ingestion client exists yet for a real put/call ratio (CBOE's delayed-quotes options
/// endpoint was verified live and no-auth, but nothing in this codebase calls it). That wiring
/// is a separate, later task - deliberately not decided or implemented here.
///
/// [REDACTED FOR PUBLIC RELEASE] The exact formula is proprietary and not included in this
/// public repository - see README.md's "What's intentionally not public" section.
/// </summary>
public static class OptionsFlowMultiplierService
{
    public static decimal ComputeMultiplier(decimal? putCallRatio, OptionsFlowMultiplierOptions options)
    {
        throw new NotImplementedException("Redacted for public release - see README.md");
    }
}
