namespace Drps.Calculator;

// Bound from configuration section "Calculator" via IOptions<CalculatorSettings>. Sat empty
// from the scheduling audit (2026-07-19, IntervalMinutes removed) until this session, which is
// exactly the "somewhere to land without re-wiring the binding from scratch" this comment
// already promised - see RsiSlopeLookback below, the first real value to use this reserved
// section.
public class CalculatorSettings
{
    // The "n" in RsiSlope[t] = RSI[t] - RSI[t-n] (CLAUDE.md, "RsiSlope / RsiConcavity: Design
    // Direction Locked", 2026-07-31) - the locked design left n as "3 or 4, TBD," deliberately
    // exposed as a config value rather than a hardcoded literal (consistent with
    // GateParameters/AdjusterParameters being versioned/configurable rather than hardcoded
    // elsewhere in this codebase) so a future recalibration is a config change, not a code
    // change. Defaulted to 3 here (not 4): 3 keeps the slope maximally responsive for the
    // "find it before it's interesting to the public" RSI edge (CLAUDE.md, Gate: Fourth Design
    // Decision) while still comparing across enough readings to filter single-day noise - a
    // longer lookback remains a one-line config change once real Drps.Monolith data suggests
    // otherwise, not a guess this file treats as final.
    public int RsiSlopeLookback { get; set; } = 3;
}
