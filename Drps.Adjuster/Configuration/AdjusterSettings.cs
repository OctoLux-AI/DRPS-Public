namespace Drps.Adjuster.Configuration;

// Bound from configuration section "Adjuster" via IOptions<AdjusterSettings>.
public class AdjusterSettings
{
    // Delay between scheduled scans once Worker's recurring loop begins a new pass.
    // Default of 15 mirrors GateSettings/CalculatorSettings' own IntervalMinutes default -
    // no sizing logic exists yet to justify a different cadence.
    public int IntervalMinutes { get; set; } = 15;
}
