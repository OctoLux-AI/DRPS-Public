namespace Drps.Execution.PreFire;

// PreFireGateService has its own fixed evaluation order, short-circuiting on the first failure -
// see that class's own doc comment for the current order and the 2026-07-27 reordering that
// changed it (deterministic/side-effect-free checks before state-mutating ones, not "cheapest
// first"). This enum's declared member order is NOT the evaluation order and is never persisted
// to the database (FailedGate/PreFireGateResult are in-memory only) - safe to leave as-is
// independent of evaluation order.
public enum PreFireGate
{
    KillSwitch,
    ConsecutiveLossCircuitBreaker,
    MarketHours,
    CashFloor,
    AssetTradable,
    ConcurrentPositionCap
}
