namespace Drps.Gate.Configuration;

// Bound from configuration section "Gate" via IOptions<GateSettings>. Empty as of the
// scheduling audit (2026-07-19) - IntervalMinutes removed, Worker now runs once daily at a
// fixed time (see Worker.cs's own doc comment), no longer configurable per this file's
// original intent. Left as a reserved, currently-unused settings class/config section rather
// than deleted outright - same "Propagated" schema-growth precedent this codebase already
// applies elsewhere (e.g. GateParameters), so a future real Gate-specific setting has
// somewhere to land without re-wiring the binding from scratch.
public class GateSettings
{
}
