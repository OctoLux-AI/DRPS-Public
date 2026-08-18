namespace Drps.Adjuster.OptionsFlow;

// Bound from configuration (intended section "OptionsFlowMultiplier" via
// IOptions<OptionsFlowMultiplierOptions>, not yet registered in Program.cs) rather than
// hardcoded, so both can be retuned once real trade data exists to calibrate against.
//
// [REDACTED FOR PUBLIC RELEASE] The values below are placeholders, not DRPS's real shipped
// tuning - see README.md's "What's intentionally not public" section.
//
// Not bound in Program.cs yet - deliberately standalone, per this task's scope. Wiring
// (registering this section, ingesting a real put/call ratio, and threading
// OptionsFlowMultiplierService into the fire path alongside insider/sentiment) is a separate,
// later task.
public class OptionsFlowMultiplierOptions
{
    public decimal NeutralThreshold { get; set; } = 0.9m;
    public decimal MultiplierCap { get; set; } = 1.2m;
}
