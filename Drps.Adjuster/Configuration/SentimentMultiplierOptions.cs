namespace Drps.Adjuster.Configuration;

// Bound from configuration (section "SentimentMultiplier" via IOptions<SentimentMultiplierOptions>)
// rather than hardcoded so they can be retuned without a code change once real data exists to
// calibrate against.
//
// [REDACTED FOR PUBLIC RELEASE] The values below are placeholders, not DRPS's real shipped
// tuning - see README.md's "What's intentionally not public" section.
//
// Not bound in Program.cs yet - deliberately standalone, per this task's scope. Wiring
// (registering this section, and threading SentimentMultiplierService into the fire path) is a
// separate, later task.
public class SentimentMultiplierOptions
{
    public decimal ScalingFactor { get; set; } = 0.5m;
    public decimal MultiplierFloor { get; set; } = 0.6m;
    public decimal MultiplierCeiling { get; set; } = 1.4m;
}
