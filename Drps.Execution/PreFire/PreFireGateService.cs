using Drps.Execution.Alpaca;
using Drps.Execution.Notifications;
using Drps.Ingestion.Persistence;
using Drps.Shared.Exceptions;
using Drps.Shared.Positioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Drps.Execution.PreFire;

/// <summary>
/// The ordered, short-circuiting pre-fire check pipeline (CLAUDE.md's Execution Layer: First
/// Design Decision, extended by the Consecutive-Loss Circuit Breaker Design Decision, the
/// PreFireGate Check Reordering decision (2026-07-27), the Concurrent-Position Cap decision
/// (2026-07-27), and the Kill-Switch Deferred to Last Check decision (2026-08-02, closing the
/// gap the 2026-07-27 reorder explicitly left open) - decides whether an already-scored, already-sized
/// candidate is actually safe to fire on right now. Six checks, evaluated in a fixed order:
/// concurrent-position cap, market-hours, consecutive-loss circuit breaker, cash floor,
/// asset-tradable, kill switch. Ordering principle (2026-07-27 correction, now applied to every
/// remaining check rather than market-hours alone - previously "cheapest/no-API-call first,"
/// which is what caused the bug the reorder fixed): a deterministic, side-effect-free check that
/// could reject on its own must run before the one check that mutates DRPS's own state (kill
/// switch), regardless of whether the deterministic check happens to require a network call.
/// Kill switch is now evaluated LAST of all six, specifically so a rejection from any of the
/// other five checks - which can never itself be predicted in advance to be "the one that would
/// have failed anyway," but is deterministic and side-effect-free in exactly the same sense
/// market-hours already was - never wastes a kill-switch strike on an attempt that was never
/// going to fire regardless. Short-circuits on the first failure - later checks never run once an
/// earlier one has already rejected the attempt, so kill switch (last) only ever increments for a
/// candidate that has already cleared every other gate.
///
/// This is a strictly later-stage check on whether execution acts on an already-made decision,
/// not a second opinion on the decision itself - it never writes to GateScore, never touches
/// Gate's own scoring, and (as of this task) is not called from any actual order-firing logic.
/// Same clock-injection pattern as GateScanService/AdjusterScanService: the as-of timestamp
/// comes from an injected `Func&lt;DateTime&gt;` seam, never a direct DateTime.Now call inside
/// this class.
/// </summary>
public class PreFireGateService
{
    private readonly IAlpacaTradingClient _tradingClient;
    private readonly DrpsDbContext _dbContext;
    private readonly KillSwitchTracker _killSwitchTracker;
    private readonly PreFireGateSettings _settings;
    private readonly IPushoverNotificationService _pushoverNotificationService;
    private readonly IInFlightPositionTracker _inFlightPositionTracker;
    private readonly ILogger<PreFireGateService> _logger;
    private readonly Func<DateTime> _nowProvider;

    public PreFireGateService(
        IAlpacaTradingClient tradingClient,
        DrpsDbContext dbContext,
        KillSwitchTracker killSwitchTracker,
        IOptions<PreFireGateSettings> settings,
        IPushoverNotificationService pushoverNotificationService,
        IInFlightPositionTracker inFlightPositionTracker,
        ILogger<PreFireGateService> logger,
        Func<DateTime>? nowProvider = null)
    {
        _tradingClient = tradingClient;
        _dbContext = dbContext;
        _killSwitchTracker = killSwitchTracker;
        _settings = settings.Value;
        _pushoverNotificationService = pushoverNotificationService;
        _inFlightPositionTracker = inFlightPositionTracker;
        _logger = logger;
        _nowProvider = nowProvider ?? (() => DateTime.Now);
    }

    public async Task<PreFireGateResult> EvaluateOpenAsync(PreFireOpenRequest request, CancellationToken cancellationToken)
    {
        var asOf = _nowProvider();

        // 1. Concurrent-position cap - a local DB count against AdjusterParameters.
        // MaxConcurrentPositions (CLAUDE.md's Adjuster: Concurrent-Position Cap Introduced,
        // 2026-07-27). Deterministic and side-effect-free (a read-only count, no external call,
        // no state mutation) - evaluated first of all six checks, ahead of even market-hours,
        // since it's cheaper still (no network round-trip). Never applied to closes - a close
        // only ever reduces the open count, so EvaluateCloseAsync omits this check entirely, same
        // reasoning as kill-switch/cash-floor's own exclusion there.
        var concurrentPositionCapFailure = await EvaluateConcurrentPositionCapAsync(request.Symbol, cancellationToken);
        if (concurrentPositionCapFailure is not null)
        {
            return concurrentPositionCapFailure;
        }

        // 2. Market-hours - a live Alpaca clock call, but deterministic and side-effect-free: it
        // never mutates this codebase's own state. Evaluated ahead of kill switch, per the fixed
        // ordering this class's own doc comment establishes (2026-07-27 correction - see that
        // comment for why this moved ahead of a DB-only check despite requiring a network call).
        var marketHoursFailure = await EvaluateMarketHoursAsync(request.Symbol, cancellationToken);
        if (marketHoursFailure is not null)
        {
            return marketHoursFailure;
        }

        // 3. Consecutive-loss circuit breaker - persisted flag, no API call. Moved ahead of kill
        // switch (2026-08-02, "Kill-Switch Deferred to Last Check" decision) - deterministic and
        // side-effect-free in exactly the same sense market-hours already was, so it belongs
        // ahead of the one state-mutating check for the identical reason. Previously ran AFTER
        // kill switch (audited-only by the 2026-07-27 reorder, which explicitly left this gap
        // open); now closed.
        var circuitBreakerFailure = await EvaluateConsecutiveLossCircuitBreakerAsync(request.Symbol, cancellationToken);
        if (circuitBreakerFailure is not null)
        {
            return circuitBreakerFailure;
        }

        // 4. Cash floor - a live Alpaca account call, but deterministic and side-effect-free
        // (reads buying power, never mutates state). Same 2026-08-02 reorder as circuit breaker
        // above - previously ran after kill switch, wasting a strike on a candidate that was
        // never going to fire once this check rejected it.
        var cashFloorFailure = await EvaluateCashFloorAsync(request, cancellationToken);
        if (cashFloorFailure is not null)
        {
            return cashFloorFailure;
        }

        // 5. Asset-tradable - never cached, always a live call, but deterministic and
        // side-effect-free. Same 2026-08-02 reorder as circuit breaker/cash floor above - now the
        // last check before kill switch, so a halted/untradable asset never consumes a strike.
        var assetFailure = await EvaluateAssetTradableAsync(request.Symbol, cancellationToken);
        if (assetFailure is not null)
        {
            return assetFailure;
        }

        // 6. Kill switch - persisted counter, mutates state (creates/increments today's row).
        // Evaluated LAST of all six checks (2026-08-02 reorder, closing the gap the 2026-07-27
        // reorder explicitly left open) - every other check has already had its chance to reject
        // first, so a rejection here is never wasted on an attempt any of the other five,
        // deterministic checks would have rejected anyway.
        var killSwitchAllowed = await _killSwitchTracker.TryRecordOpenAttemptAsync(
            asOf, _settings.KillSwitchMaxOpensPerDay, cancellationToken);

        if (!killSwitchAllowed)
        {
            var reason = $"Kill switch tripped: {_settings.KillSwitchMaxOpensPerDay} order-open attempt(s) already " +
                         "reached for today's trading day";

            // The one pre-fire gate rejection significant enough to warrant a push alert
            // (CLAUDE.md's Execution Layer: Ninth Design Decision) - a runaway-buy-loop
            // scenario, unlike an ordinary market-closed/cash-floor/asset-not-tradable
            // rejection, which stays log-only via the shared Fail() helper below. Deduped to
            // once per trading day per trip (2026-07-24 correction) - without
            // TryMarkNotifiedAsync's own-day check, a poll-interval re-check
            // (OrchestrationSettings.PollIntervalSeconds, default 60) re-sends this same
            // notification every single cycle for as long as the tripped state persists. The
            // rejection itself (Fail() below) is returned identically regardless of whether the
            // notification actually sent - dedup only affects the side-effect, never the gate
            // outcome.
            var shouldNotify = await _killSwitchTracker.TryMarkNotifiedAsync(asOf, cancellationToken);
            if (shouldNotify)
            {
                await _pushoverNotificationService.SendAsync(
                    $"DRPS: kill switch tripped - {request.Symbol}: {reason}", cancellationToken);
            }

            return Fail(request.Symbol, PreFireGate.KillSwitch, reason);
        }

        _logger.LogInformation("[PRE-FIRE-GATE]: {Symbol}: all six checks passed, safe to fire", request.Symbol);
        return new PreFireGateResult { Passed = true };
    }

    /// <summary>
    /// CLAUDE.md's Execution Layer: Consecutive-Loss Circuit Breaker Design Decision, points 5
    /// and 7. Deliberately NOT a KillSwitchTracker-shaped class: the write side (incrementing/
    /// tripping the counter) lives in Drps.Ledger's LedgerPositionWriter.ClosePositionAsync, not
    /// here - Drps.Ledger cannot reference Drps.Execution (the reverse reference already exists,
    /// via FillConfirmationService), so a shared tracker class spanning both projects isn't
    /// possible. This method only ever reads the persisted Tripped flag directly via
    /// DrpsDbContext (same "read the table directly, not via a project reference" pattern
    /// EvaluateCashFloorAsync already uses for AdjusterParameters), plus writes back NotifiedAt
    /// for the one-alert-per-trip dedup - it never touches ConsecutiveLossCount or Tripped
    /// itself.
    /// </summary>
    private async Task<PreFireGateResult?> EvaluateConsecutiveLossCircuitBreakerAsync(string symbol, CancellationToken cancellationToken)
    {
        var breaker = await _dbContext.ConsecutiveLossCircuitBreakers.SingleOrDefaultAsync(cancellationToken);

        if (breaker is null || !breaker.Tripped)
        {
            return null;
        }

        var reason = $"Consecutive-loss circuit breaker tripped: {breaker.ConsecutiveLossCount} consecutive " +
                     "realized losses recorded. Requires a manual reset (Drps.Ledger's reset-circuit-breaker " +
                     "CLI command) before new opens resume.";

        // Dedup per the design decision's point 7 - same shape as KillSwitchCounter.NotifiedAt
        // (notify once per trip, not once per poll cycle), but cleared only by the manual reset
        // action (point 4) rather than a calendar boundary, since none exists here.
        if (breaker.NotifiedAt is null)
        {
            breaker.NotifiedAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);

            await _pushoverNotificationService.SendAsync(
                $"DRPS: consecutive-loss circuit breaker tripped - {symbol}: {reason}", cancellationToken);
        }

        return Fail(symbol, PreFireGate.ConsecutiveLossCircuitBreaker, reason);
    }

    /// <summary>
    /// Close-side counterpart to EvaluateOpenAsync. Deliberately runs only two of the six
    /// open-side checks - Market-Hours and Asset-Tradable - never Kill-Switch, Consecutive-Loss
    /// Circuit Breaker, Cash-Floor, or Concurrent-Position-Cap:
    ///
    /// - Kill-switch: CLAUDE.md's Execution Layer: First Design Decision states explicitly that
    ///   the kill switch counts order-OPEN attempts only, closes are excluded. Calling
    ///   KillSwitchTracker here would both incorrectly consume budget meant for opens and let a
    ///   tripped kill switch block a close - the opposite of what the kill switch exists for
    ///   (stopping a runaway BUY loop, not blocking an exit).
    /// - Cash-floor: not named in that design decision at all, but excluded here for a distinct,
    ///   equally real reason found by reading EvaluateCashFloorAsync's own logic directly - a
    ///   close releases capital (a filled sell increases buying power), it never consumes it, so
    ///   the check's entire premise (does this order's dollar cost push remaining buying power
    ///   below the reserve floor) does not apply to a sell. Worse, running it anyway risks a
    ///   perverse outcome: if buying power is already below the floor for an unrelated reason,
    ///   the floor check would reject the close too - blocking exactly the action (selling,
    ///   freeing capital) that would help restore the floor. Never evaluated for closes.
    /// - Concurrent-position-cap (2026-07-27): same reasoning as cash-floor's exclusion above,
    ///   in the opposite direction - a close only ever REDUCES the open-position count, it never
    ///   adds to it, so the cap's entire premise (would opening this position push the open count
    ///   past the limit) cannot apply to a sell. Blocking a close on this check would be the same
    ///   perverse "block the action that would fix the underlying condition" outcome cash-floor's
    ///   exclusion already guards against.
    /// - Consecutive-loss circuit breaker: excluded per that design decision's own point 3 -
    ///   blocking an exit because the strategy is underperforming would be perverse, preventing
    ///   exactly the action (cutting a loss, or taking a win) that should happen freely regardless
    ///   of a broader losing streak.
    /// </summary>
    public async Task<PreFireGateResult> EvaluateCloseAsync(PreFireCloseRequest request, CancellationToken cancellationToken)
    {
        var marketHoursFailure = await EvaluateMarketHoursAsync(request.Symbol, cancellationToken);
        if (marketHoursFailure is not null)
        {
            return marketHoursFailure;
        }

        var assetFailure = await EvaluateAssetTradableAsync(request.Symbol, cancellationToken);
        if (assetFailure is not null)
        {
            return assetFailure;
        }

        _logger.LogInformation(
            "[PRE-FIRE-GATE]: {Symbol}: close checks passed (market-hours, asset-tradable) - safe to fire", request.Symbol);
        return new PreFireGateResult { Passed = true };
    }

    /// <summary>
    /// CLAUDE.md's Adjuster: Concurrent-Position Cap Introduced (2026-07-27) - DRPS's first hard
    /// limit on simultaneously-open Position rows, superseding the 2026-07-15 "no independent
    /// cap, let capital/sector/sizing bound it naturally" decision. Reads AdjusterParameters
    /// directly (same "read the table directly, not via a project reference" pattern
    /// EvaluateCashFloorAsync already uses, and the same fail-closed "exactly one active row"
    /// requirement) - deliberately a separate query rather than sharing EvaluateCashFloorAsync's
    /// fetch, since this check is evaluated at step 1 (open-side only) while cash-floor remains
    /// at step 4 (2026-08-02 reorder - see this class's own doc comment); a shared prefetch would
    /// require restructuring both methods around a common caller-supplied parameters row, which
    /// this task deliberately did not do (kept each check self-contained, matching every other
    /// check in this class).
    ///
    /// The actual accept/reject decision is delegated to IInFlightPositionTracker.
    /// TryReserveOpenSlot, not a bare `openPositionCount >= max` comparison - see that method's
    /// own doc comment for the concurrent-detached-task race this closes (same-day follow-up to
    /// this check's original, race-prone version). A `true` result here means a slot is now
    /// reserved under `symbol` in the tracker and MUST be released later via
    /// IInFlightPositionTracker.Release(symbol) once this candidate's processing fully resolves -
    /// OrchestrationWorker's existing finally block (which already releases the same ticker's
    /// per-ticker in-flight marker for every outcome) is what does this in production.
    /// </summary>
    private async Task<PreFireGateResult?> EvaluateConcurrentPositionCapAsync(string symbol, CancellationToken cancellationToken)
    {
        var activeParametersRows = await _dbContext.AdjusterParameters
            .Where(p => p.IsActive)
            .ToListAsync(cancellationToken);

        if (activeParametersRows.Count != 1)
        {
            _logger.LogCritical(
                "[PRE-FIRE-GATE]: {Symbol}: concurrent-position-cap check aborted - {Count} active AdjusterParameters row(s) found (expected exactly 1)",
                symbol, activeParametersRows.Count);
            return Fail(
                symbol, PreFireGate.ConcurrentPositionCap,
                "Cannot verify concurrent-position cap: AdjusterParameters is not in a valid single-active-row state");
        }

        var maxConcurrentPositions = activeParametersRows[0].MaxConcurrentPositions;
        var openPositionCount = await _dbContext.Positions.CountAsync(p => p.ExitDate == null, cancellationToken);

        if (!_inFlightPositionTracker.TryReserveOpenSlot(symbol, openPositionCount, maxConcurrentPositions))
        {
            _logger.LogInformation(
                "[PRE-FIRE-GATE]: {Symbol}: concurrent-position cap reached - {OpenCount}/{Max} positions currently open " +
                "(counting in-flight reservations from candidates firing concurrently this cycle)",
                symbol, openPositionCount, maxConcurrentPositions);

            return Fail(
                symbol, PreFireGate.ConcurrentPositionCap,
                $"Concurrent-position cap reached ({openPositionCount}/{maxConcurrentPositions} positions currently open, " +
                "including in-flight reservations)");
        }

        return null;
    }

    private async Task<PreFireGateResult?> EvaluateMarketHoursAsync(string symbol, CancellationToken cancellationToken)
    {
        var clockResult = await _tradingClient.GetClockAsync(cancellationToken);

        if (!clockResult.Success)
        {
            return Fail(symbol, PreFireGate.MarketHours, $"Failed to confirm market hours: {clockResult.ErrorMessage}");
        }

        if (clockResult.IsOpen != true)
        {
            return Fail(symbol, PreFireGate.MarketHours, "Market is closed (regular-hours-only, V1)");
        }

        return null;
    }

    private async Task<PreFireGateResult?> EvaluateCashFloorAsync(PreFireOpenRequest request, CancellationToken cancellationToken)
    {
        // Fail-closed: the floor can never be computed against a guessed/bootstrap reserve
        // schedule - same shape as GateScanService/AdjusterScanService's own active-parameters
        // check. Reads AdjusterParameters directly (not via a Drps.Adjuster project reference -
        // see CapitalReserveCalculator's own doc comment for why).
        var activeParametersRows = await _dbContext.AdjusterParameters
            .Where(p => p.IsActive)
            .ToListAsync(cancellationToken);

        // Currently unreachable via EvaluateOpenAsync's real call path, confirmed directly - kept
        // intentionally, not a forgotten dead branch (CLAUDE.md's "EvaluateCashFloorAsync's
        // Unreachable Fail-Closed Branch" addendum, 2026-07-28, has the full record). This
        // method is private with exactly one call site (step 4 in EvaluateOpenAsync's fixed
        // order as of the 2026-08-02 reorder - was step 5 before kill switch moved to last), and
        // EvaluateConcurrentPositionCapAsync (step 1) already fails closed on this exact same
        // "AdjusterParameters is not in a valid single-active-row state" condition, so by the
        // time this check runs, at least one active row is already guaranteed to exist.
        // Left in place as defense-in-depth: removing a fail-closed safety check is riskier than
        // leaving an inert one - a future reorder of the six-check pipeline (already happened
        // twice now - 2026-07-27 and 2026-08-02) could make this reachable again, and this branch
        // being present means that reorder fails closed automatically rather than needing to be
        // remembered and re-added. See EvaluateOpenAsync_ConcurrentPositionCapAlwaysPrecedesCashFloor
        // below for the test that fails loudly if this ordering assumption is ever broken.
        if (activeParametersRows.Count != 1)
        {
            _logger.LogCritical(
                "[PRE-FIRE-GATE]: {Symbol}: cash-floor check aborted - {Count} active AdjusterParameters row(s) found (expected exactly 1)",
                request.Symbol, activeParametersRows.Count);
            return Fail(
                request.Symbol, PreFireGate.CashFloor,
                "Cannot verify reserve tier: AdjusterParameters is not in a valid single-active-row state");
        }

        var parameters = activeParametersRows[0];

        var accountResult = await _tradingClient.GetAccountAsync(cancellationToken);
        if (!accountResult.Success || accountResult.BuyingPower is null)
        {
            return Fail(request.Symbol, PreFireGate.CashFloor, $"Failed to fetch live buying power: {accountResult.ErrorMessage}");
        }

        // "Total capital" for reserve-tier purposes is BuyingPower - same convention
        // AdjusterScanService already establishes (its own RunScanAsync treats
        // AlpacaAccountFeeder's BuyingPower as totalCapital), reused here rather than
        // introducing a second, inconsistent notion of "total capital".
        var buyingPower = accountResult.BuyingPower.Value;
        var reserveAdjustedAvailable = CapitalReserveCalculator.ComputeReserveAdjustedAvailableCapital(parameters, buyingPower);
        var floor = buyingPower - reserveAdjustedAvailable;
        var remainingAfterOrder = buyingPower - request.ProposedDollarAmount;

        if (remainingAfterOrder < floor)
        {
            _logger.LogInformation(
                "[PRE-FIRE-GATE]: {Symbol}: cash floor breached - order of {ProposedDollarAmount:F2} would leave " +
                "{RemainingAfterOrder:F2}, below the required {Floor:F2} reserve floor",
                request.Symbol, request.ProposedDollarAmount, remainingAfterOrder, floor);

            // Skipped, never truncated (CLAUDE.md's Execution Layer: First Design Decision) -
            // this returns a rejection, it never adjusts ProposedDollarAmount downward.
            return Fail(
                request.Symbol, PreFireGate.CashFloor,
                $"Order would breach the reserve floor (remaining {remainingAfterOrder:F2} < required {floor:F2})");
        }

        return null;
    }

    private async Task<PreFireGateResult?> EvaluateAssetTradableAsync(string symbol, CancellationToken cancellationToken)
    {
        AlpacaAssetResult assetResult;
        try
        {
            // Deliberately never cached anywhere (CLAUDE.md's Execution Layer: First Design
            // Decision) - a halt is a real-time event, unlike the 7-day-TTL sector/ex-div
            // reference data elsewhere in this codebase.
            assetResult = await _tradingClient.GetAssetAsync(symbol, cancellationToken);
        }
        catch (SymbolNotFoundException ex)
        {
            // Not a kill-switch strike - as of the 2026-08-02 reorder, kill switch is step 6
            // (last) and has not run yet at this point, so this rejection never reaches it at
            // all. This is simply the asset-tradable gate's own rejection.
            return Fail(symbol, PreFireGate.AssetTradable, $"Alpaca has no asset record for symbol '{symbol}' ({ex.Message})");
        }

        if (!assetResult.Success)
        {
            return Fail(symbol, PreFireGate.AssetTradable, $"Failed to fetch asset status: {assetResult.ErrorMessage}");
        }

        if (assetResult.Tradable != true)
        {
            return Fail(symbol, PreFireGate.AssetTradable, $"Asset is not currently tradable (status={assetResult.Status})");
        }

        return null;
    }

    private PreFireGateResult Fail(string symbol, PreFireGate gate, string reason)
    {
        // Each gate logs its own distinct outcome - never collapsed into one generic
        // rejection reason, per this task's explicit requirement.
        _logger.LogWarning("[PRE-FIRE-GATE]: {Symbol}: rejected at {Gate} - {Reason}", symbol, gate, reason);
        return new PreFireGateResult { Passed = false, FailedGate = gate, Reason = reason };
    }
}
