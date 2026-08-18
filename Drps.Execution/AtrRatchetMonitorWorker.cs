using Drps.Execution.Alpaca;
using Drps.Execution.Firing;
using Drps.Ingestion.Persistence;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Drps.Execution;

/// <summary>
/// The ATR ratchet stop, live-monitored (CLAUDE.md's Execution Layer: Third Design Decision) -
/// the last piece named as "still doesn't exist anywhere" by the 2026-07-17 exit-path audit.
/// Lives in Execution, not Gate: ATR was already excluded from Gate's composite/ranking math
/// (Sixth Decision) because it answers a proportionality/stability question, not a signal-
/// ranking one, and a stop breach is a mechanical trigger that needs a far tighter polling
/// cadence than Gate's own scan cycle - a genuinely new, independent BackgroundService rather
/// than a variant of OrchestrationWorker's candidate-discovery loop (a different concern,
/// polling at a different rate, per AtrMonitorSettings).
///
/// Every poll, for every open Position carrying a real EntryAtr/TpTargetPrice ratchet baseline:
/// fetches a live quote (midpoint of bid/ask - this client has no separate last-trade method)
/// and the ticker's most recent GateScore.AtrValue (the CURRENT, live ATR - never EntryAtr,
/// which is fixed at entry and never changes), raises HighWaterMark if the current price is a
/// new high, computes a tightening ATR-multiple stop level (3.0x at progress=0 down to 1.0x at
/// progress=1, per CLAUDE.md's locked placeholders), and fires a close on breach.
///
/// In-flight tracking is delegated to the SAME shared IInFlightPositionTracker singleton
/// OrchestrationWorker uses (CLAUDE.md's Execution Layer: Sixth Design Decision) - a ticker
/// OrchestrationWorker is already mid-close on must not also be fired here, and vice versa.
/// A breach is marked in-flight only at the moment it is about to fire (not for every position
/// checked every poll) - most polls find no breach at all, so marking every open ticker
/// in-flight on every poll would needlessly block OrchestrationWorker's own close consideration
/// for tickers with nothing wrong.
///
/// A breach's fire-then-confirm sequence runs as its own independent, detached task - same
/// reasoning as OrchestrationWorker: a fractional Day-TIF fill-confirmation wait can take up to
/// several minutes and must not block the rest of this poll's position checks or the next poll
/// cycle. RunCycleAsync returns the spawned tasks purely so tests can await them
/// deterministically - production code (ExecuteAsync) does not await them individually.
/// </summary>
public class AtrRatchetMonitorWorker : BackgroundService
{
    // Chandelier-style ratchet multiplier bounds (CLAUDE.md's Execution Layer: Third Design
    // Decision) - 3.0x ATR at progress=0 (standard Chandelier starting point), tightening
    // linearly to a 1.0x ATR floor as progress toward TpTargetPrice approaches 1. Explicit
    // placeholders pending real Drps.Monolith calibration, not tuned values.
    private const decimal MaxMultiplier = 3.0m;
    private const decimal MinMultiplier = 1.0m;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<AtrMonitorSettings> _settings;
    private readonly ILogger<AtrRatchetMonitorWorker> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IInFlightPositionTracker _inFlightTracker;

    public AtrRatchetMonitorWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<AtrMonitorSettings> settings,
        ILogger<AtrRatchetMonitorWorker> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IInFlightPositionTracker? inFlightTracker = null)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _logger = logger;
        // Injectable so tests can make the between-cycle wait resolve instantly - same seam
        // pattern as OrchestrationWorker/OrderFiringService/FillConfirmationService's own
        // injectable delay.
        _delay = delay ?? Task.Delay;
        // Defaults to a private instance (e.g. for a test that doesn't care about sharing) -
        // production DI registers one singleton instance shared with OrchestrationWorker.
        _inFlightTracker = inFlightTracker ?? new InFlightPositionTracker();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ends only via stoppingToken cancellation (Ctrl+C, host shutdown) - never on its own,
        // same as every other Worker in this codebase.
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCycleAsync(stoppingToken);
            await _delay(TimeSpan.FromSeconds(_settings.Value.PollIntervalSeconds), stoppingToken);
        }
    }

    /// <summary>
    /// Evaluates every open Position with a real ratchet baseline for a stop breach, persists
    /// any HighWaterMark updates, and spawns one detached fire-then-confirm task per genuine,
    /// not-already-in-flight breach. Returns immediately once spawning (not processing) is done.
    /// </summary>
    public async Task<IReadOnlyList<Task>> RunCycleAsync(CancellationToken stoppingToken)
    {
        var breaches = new List<BreachedPosition>();

        // Position discovery, per-position evaluation, and the HighWaterMark write-back all
        // share one scope/DbContext - unlike the fire-then-confirm sequence below, none of this
        // is long-running enough to need its own detached task, and batching the HighWaterMark
        // updates into one SaveChangesAsync at the end of the loop is simpler than one write
        // per position.
        using (var scope = _scopeFactory.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<DrpsDbContext>();
            var tradingClient = scope.ServiceProvider.GetRequiredService<IAlpacaTradingClient>();

            List<Position> openPositions;
            try
            {
                openPositions = await dbContext.Positions
                    .Where(p => p.ExitDate == null)
                    .ToListAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ATR-RATCHET-MONITOR]: open-position discovery failed this cycle - no positions checked");
                return Array.Empty<Task>();
            }

            foreach (var position in openPositions)
            {
                try
                {
                    var breach = await EvaluatePositionAsync(position, dbContext, tradingClient, stoppingToken);
                    if (breach is not null)
                    {
                        breaches.Add(breach.Value);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex, "[ATR-RATCHET-MONITOR]: {Ticker}: unhandled exception evaluating Position {PositionId}",
                        position.Ticker, position.Id);
                }
            }

            try
            {
                await dbContext.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ATR-RATCHET-MONITOR]: failed to persist HighWaterMark updates this cycle");
            }
        }

        var spawned = new List<Task>();

        foreach (var breach in breaches)
        {
            var ticker = breach.Position.Ticker;

            // Marked in-flight only now, at the moment a genuine breach is about to fire - not
            // for every open position checked every poll (see class-level comment). Shared with
            // OrchestrationWorker: if that worker is already mid-close on this same ticker right
            // now, skip this poll's breach rather than firing a second close.
            if (!_inFlightTracker.TryMarkInFlight(ticker))
            {
                _logger.LogInformation(
                    "[ATR-RATCHET-MONITOR]: {Ticker}: ATR stop breach detected but already in-flight - skipping this poll",
                    ticker);
                continue;
            }

            spawned.Add(ProcessBreachAsync(breach, stoppingToken));
        }

        return spawned;
    }

    // Returns null for: a missing ratchet baseline (logged, skipped), no GateScore row for this
    // ticker at all (logged, skipped), a failed quote fetch (logged, skipped), or no breach.
    // HighWaterMark is updated on `position` in-memory either way (persisted by the caller's
    // single end-of-loop SaveChangesAsync) whenever a real evaluation actually occurs.
    private async Task<BreachedPosition?> EvaluatePositionAsync(
        Position position, DrpsDbContext dbContext, IAlpacaTradingClient tradingClient, CancellationToken cancellationToken)
    {
        var ticker = position.Ticker;

        // Scope: every open Position where EntryAtr AND TpTargetPrice are both non-null. A
        // legacy/manually-seeded row missing either has no real ratchet baseline to evaluate
        // against - skipped (logged), never guessed at with a synthetic baseline.
        if (position.EntryAtr is null || position.TpTargetPrice is null)
        {
            _logger.LogInformation(
                "[ATR-RATCHET-MONITOR]: {Ticker}: Position {PositionId} missing EntryAtr/TpTargetPrice - " +
                "skipping (legacy/manually-seeded row with no ratchet baseline)",
                ticker, position.Id);
            return null;
        }

        // Current (live) ATR: the most recent NON-REJECTED GateScore row for this ticker -
        // NOT EntryAtr, which is fixed at entry and never changes. RejectionReason == null
        // excludes rejection rows (CLAUDE.md's "Gate: Rejection Reasons Now Persisted,"
        // 2026-08-06) - an open Position's ticker is evaluated by Gate on every scan exactly
        // like any other candidate (no position-status filter in candidate discovery) and can
        // be rejected on a later ScanDate than its last real scored row. Deliberately NOT a
        // Bucket filter: rejection rows reuse Bucket=Neutral, the same bucket genuine
        // low-composite-score candidates already use, so filtering on Bucket would exclude
        // real, trustworthy rows too.
        var latestGateScore = await dbContext.GateScores
            .Where(g => g.Ticker == ticker && g.RejectionReason == null)
            .OrderByDescending(g => g.ScanDate)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestGateScore is null)
        {
            _logger.LogWarning(
                "[ATR-RATCHET-MONITOR]: {Ticker}: Position {PositionId} has no GateScore row at all - cannot " +
                "resolve a current live ATR, skipping this poll",
                ticker, position.Id);
            return null;
        }

        var currentAtr = latestGateScore.AtrValue;

        // Current price source: quote midpoint - this client has no separate last-trade method,
        // so the bid/ask midpoint stands in as "current price" for both the high-water-mark
        // update and the stop check below.
        AlpacaQuoteResult quote;
        try
        {
            quote = await tradingClient.GetLatestQuoteAsync(ticker, cancellationToken);
        }
        catch (SymbolNotFoundException ex)
        {
            _logger.LogWarning("[ATR-RATCHET-MONITOR]: {Ticker}: quote fetch failed - {Message}", ticker, ex.Message);
            return null;
        }

        if (!quote.Success || quote.Bid is null || quote.Ask is null)
        {
            _logger.LogWarning("[ATR-RATCHET-MONITOR]: {Ticker}: quote fetch failed - {ErrorMessage}", ticker, quote.ErrorMessage);
            return null;
        }

        var currentPrice = (quote.Bid.Value + quote.Ask.Value) / 2m;

        // a. High-water mark only ever moves up. Null (a position with a real EntryAtr/
        // TpTargetPrice baseline but predating this specific column) falls back to EntryPrice -
        // the same value it would have been seeded to - and self-heals from here on.
        var highWaterMark = position.HighWaterMark ?? position.EntryPrice;
        if (currentPrice > highWaterMark)
        {
            highWaterMark = currentPrice;
        }
        position.HighWaterMark = highWaterMark;

        // b. Progress toward TpTargetPrice. Guarded against a non-positive target distance
        // (should never happen - TpTargetPrice = EntryPrice + N x EntryAtr with a positive ATR -
        // but defensively treated as zero progress rather than dividing by a non-positive
        // number, same "exclude rather than compute garbage" precedent as
        // BarReconciliationService's Close<=0 guard). rawProgress is retained uncapped for
        // TpProgressAtExit at breach time; clampedProgress is [0,1] for the multiplier only.
        var targetDistance = position.TpTargetPrice.Value - position.EntryPrice;
        var rawProgress = targetDistance > 0m
            ? (currentPrice - position.EntryPrice) / targetDistance
            : 0m;
        var clampedProgress = Math.Clamp(rawProgress, 0m, 1m);

        // c. Linear interpolation: 3.0x ATR at progress=0, tightening to a 1.0x ATR floor as
        // progress -> 1.
        var multiplier = MaxMultiplier - ((MaxMultiplier - MinMultiplier) * clampedProgress);

        // d. Breach check.
        var stopLevel = highWaterMark - (multiplier * currentAtr);

        _logger.LogInformation(
            "[ATR-RATCHET-MONITOR]: {Ticker}: Position {PositionId} currentPrice={CurrentPrice} " +
            "highWaterMark={HighWaterMark} currentAtr={CurrentAtr} multiplier={Multiplier} stopLevel={StopLevel}",
            ticker, position.Id, currentPrice, highWaterMark, currentAtr, multiplier, stopLevel);

        if (currentPrice < stopLevel)
        {
            return new BreachedPosition(position, rawProgress);
        }

        return null;
    }

    private async Task ProcessBreachAsync(BreachedPosition breach, CancellationToken stoppingToken)
    {
        var position = breach.Position;
        var ticker = position.Ticker;

        try
        {
            var settings = _settings.Value;

            if (settings.IsDryRunEnabled)
            {
                // Dry-run: log exactly what would have fired, using only context already
                // available from the breach itself - no live quote is re-fetched here, same
                // "don't fabricate what FireCloseAsync would compute" reasoning as
                // OrchestrationWorker's own dry-run branches.
                _logger.LogInformation(
                    "[ATR-RATCHET-MONITOR]: DRY-RUN: would fire CLOSE for {Ticker} (PositionId={PositionId}, " +
                    "EntryQuantity={EntryQuantity}, EntryPrice={EntryPrice}, ExitReason={ExitReason}, " +
                    "TpProgressAtExit={TpProgressAtExit})",
                    ticker, position.Id, position.EntryQuantity, position.EntryPrice,
                    PositionExitReason.AtrStop, breach.RawProgress);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var orderFiringService = scope.ServiceProvider.GetRequiredService<OrderFiringService>();

            var fireResult = await orderFiringService.FireCloseAsync(position, stoppingToken);

            // Only proceed to fill-confirmation if firing actually placed a real order - every
            // other outcome (pre-fire-gate rejection, zero-quantity skip, quote-fetch failure,
            // broker rejection, or an unresolved ambiguous failure) means no real order exists
            // for FillConfirmationService to poll.
            if (fireResult.Outcome != OrderFiringOutcome.Fired)
            {
                _logger.LogWarning(
                    "[ATR-RATCHET-MONITOR]: {Ticker}: ATR stop close not fired - {Outcome} ({Reason})",
                    ticker, fireResult.Outcome, fireResult.Reason);
                return;
            }

            var fillConfirmationService = scope.ServiceProvider.GetRequiredService<FillConfirmationService>();
            var maxWait = TimeSpan.FromSeconds(_settings.Value.FillConfirmationMaxWaitSeconds);

            var confirmResult = await fillConfirmationService.ConfirmCloseAsync(
                fireResult.ClientOrderId!, position, PositionExitReason.AtrStop, maxWait, stoppingToken,
                tpProgressAtExit: breach.RawProgress);

            _logger.LogInformation(
                "[ATR-RATCHET-MONITOR]: {Ticker}: ATR stop fill-confirmation outcome {Outcome}", ticker, confirmResult.Outcome);
        }
        catch (Exception ex)
        {
            // This method runs detached from RunCycleAsync/ExecuteAsync - an unhandled
            // exception here would otherwise become an unobserved task exception rather than a
            // loop crash, but it must still be logged, not silently lost. No breach's exception
            // may crash the polling loop, same discipline as OrchestrationWorker.
            _logger.LogError(ex, "[ATR-RATCHET-MONITOR]: {Ticker}: unhandled exception processing ATR stop breach", ticker);
        }
        finally
        {
            // Covers every outcome above: pre-fire-gate rejection, terminal firing failure,
            // fill-confirmation timeout, and an unhandled exception. No breach's failure may
            // leave its ticker stuck in-flight forever.
            _inFlightTracker.Release(ticker);
        }
    }

    private readonly record struct BreachedPosition(Position Position, decimal RawProgress);
}
