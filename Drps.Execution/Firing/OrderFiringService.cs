using Drps.Adjuster.Configuration;
using Drps.Adjuster.Sentiment;
using Drps.Adjuster.Sizing;
using Drps.Execution.Alpaca;
using Drps.Execution.Notifications;
using Drps.Execution.PreFire;
using Drps.Ingestion.Persistence;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Microsoft.Extensions.Options;

namespace Drps.Execution.Firing;

/// <summary>
/// Turns an approved GateScore + AdjusterAllocation pair into a real Alpaca order (open/buy
/// side only - PreFireGateService's own scope is order-OPEN attempts, per CLAUDE.md's
/// Execution Layer: First Design Decision). This service owns calling the pre-fire gate
/// pipeline itself before ever attempting an order - it is never the caller's responsibility
/// to have checked gates beforehand.
///
/// Deliberately does NOT poll for fill confirmation and does NOT write anything to
/// Drps.Ledger/Position - both are separate, later tasks (CLAUDE.md's Execution Layer: Fourth
/// Design Decision). This class fires the order and handles only its immediate retry
/// classification (CLAUDE.md's Execution Layer: Second Design Decision) and, for a sub-1-share
/// order specifically, a single scheduled follow-up cancel-if-unfilled check.
/// </summary>
public class OrderFiringService
{
    // Ask + 0.25% for a buy - this task's own explicit marketable-limit percentage.
    private const decimal MarketableLimitBuffer = 0.0025m;

    private static readonly TimeSpan FractionalOrderCancelDelay = TimeSpan.FromMinutes(5);

    private readonly IAlpacaTradingClient _tradingClient;
    private readonly DrpsDbContext _dbContext;
    private readonly PreFireGateService _preFireGateService;
    private readonly SentimentMultiplierService _sentimentMultiplierService;
    private readonly IPushoverNotificationService _pushoverNotificationService;
    private readonly ILogger<OrderFiringService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly MultiSignalWeightOptions _multiSignalWeights;

    public OrderFiringService(
        IAlpacaTradingClient tradingClient,
        // CLAUDE.md's 2026-07-31 retry-ambiguity audit, Gap 1 - required, no default, same
        // reasoning as every other provenance/persistence dependency in this codebase (Position.
        // OpenOrigin's own doc comment: an optional-with-default seam is exactly what turns a
        // real signal into an accidental, non-authoritative correlate). Used only to write an
        // AmbiguousFireSkip row when an outcome resolves to AmbiguousUnresolved - this class
        // still writes nothing to Position/Ledger, per its own class-level doc comment above.
        DrpsDbContext dbContext,
        PreFireGateService preFireGateService,
        SentimentMultiplierService sentimentMultiplierService,
        IPushoverNotificationService pushoverNotificationService,
        ILogger<OrderFiringService> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        // CLAUDE.md's Adjuster: Multi-Signal Multiplier Combination - Weighted-Deviation-Sum
        // Model (2026-07-26). Optional with a real (not null-object) default, same pattern as
        // `delay` above, so every pre-existing test call site that doesn't pass this keeps
        // compiling and keeps seeing the day-one default weights (1.0/1.0) - the DI container
        // still supplies the real, configuration-bound instance in production once
        // Configure<MultiSignalWeightOptions> is registered in Program.cs.
        IOptions<MultiSignalWeightOptions>? multiSignalWeightOptions = null)
    {
        _tradingClient = tradingClient;
        _dbContext = dbContext;
        _preFireGateService = preFireGateService;
        _sentimentMultiplierService = sentimentMultiplierService;
        _pushoverNotificationService = pushoverNotificationService;
        _logger = logger;
        // Injectable so tests can make the sub-1-share branch's 5-minute follow-up resolve
        // instantly, same clock/delay-seam discipline as GateScanService's nowProvider.
        _delay = delay ?? Task.Delay;
        _multiSignalWeights = multiSignalWeightOptions?.Value ?? new MultiSignalWeightOptions();
    }

    // `attempt` is part of the client_order_id format this task specifies
    // (drps-{GateScoreId}-{AdjusterAllocationId}-{attempt}) - it identifies genuinely separate
    // top-level firing attempts for the same GateScore/AdjusterAllocation pair (e.g. a later,
    // independent re-fire after this call already returned). It is NOT incremented for the
    // single internal ambiguous-failure retry below - that retry deliberately reuses the exact
    // same client_order_id so Alpaca's own duplicate-order protection guards against a real
    // double-fire if the original request actually landed.
    public async Task<OrderFiringResult> FireAsync(
        GateScore gateScore, AdjusterAllocation allocation, CancellationToken cancellationToken, int attempt = 1)
    {
        var symbol = gateScore.Ticker;

        // a. PreFireGateService owns deciding whether firing is safe right now.
        var gateResult = await _preFireGateService.EvaluateOpenAsync(
            new PreFireOpenRequest { Symbol = symbol, ProposedDollarAmount = allocation.AllocationDollarAmount },
            cancellationToken);

        if (!gateResult.Passed)
        {
            _logger.LogWarning(
                "[ORDER-FIRING]: {Symbol}: not fired - rejected by pre-fire gate {FailedGate} ({Reason})",
                symbol, gateResult.FailedGate, gateResult.Reason);
            return new OrderFiringResult
            {
                Outcome = OrderFiringOutcome.RejectedByPreFireGate,
                Reason = $"{gateResult.FailedGate}: {gateResult.Reason}"
            };
        }

        // Adjuster's own already-established skip category - no live quote or order call is
        // needed for an allocation Adjuster already flagged as unfundable.
        if (allocation.ShareCapDeficient)
        {
            _logger.LogInformation("[ORDER-FIRING]: {Symbol}: skipped - AdjusterAllocation is ShareCapDeficient", symbol);
            return new OrderFiringResult
            {
                Outcome = OrderFiringOutcome.SkippedZeroQuantity,
                Reason = "AdjusterAllocation.ShareCapDeficient"
            };
        }

        // b. Marketable-limit price from a live quote - a scan-time Close is stale by firing
        // time, so the PRICE still has to be live. The QUANTITY does not: see the ShareCount
        // comment below.
        AlpacaQuoteResult quoteResult;
        try
        {
            quoteResult = await _tradingClient.GetLatestQuoteAsync(symbol, cancellationToken);
        }
        catch (SymbolNotFoundException ex)
        {
            _logger.LogWarning("[ORDER-FIRING]: {Symbol}: quote fetch failed - {Message}", symbol, ex.Message);
            return new OrderFiringResult { Outcome = OrderFiringOutcome.QuoteFetchFailed, Reason = ex.Message };
        }

        if (!quoteResult.Success || quoteResult.Ask is null)
        {
            _logger.LogWarning("[ORDER-FIRING]: {Symbol}: quote fetch failed - {ErrorMessage}", symbol, quoteResult.ErrorMessage);
            return new OrderFiringResult { Outcome = OrderFiringOutcome.QuoteFetchFailed, Reason = quoteResult.ErrorMessage };
        }

        // Rounded UP to the nearest cent, never down - a buy limit rounded down risks landing
        // below the intended marketable price. Explicit Math.Ceiling, not Math.Round (default
        // MidpointRounding.ToEven is wrong in this direction) - confirmed necessary by a real
        // rejected fire (Ask=214.14m produced an unrounded 214.67535m limit_price, rejected by
        // Alpaca with a sub-penny-increment 422). Deliberately whole-cent only - sub-$1.00
        // sub-penny tick sizing (SEC Rule 612) is out of scope for this fix.
        var limitPrice = Math.Ceiling(quoteResult.Ask.Value * (1m + MarketableLimitBuffer) * 100m) / 100m;

        // Quantity comes directly from AdjusterAllocation.ShareCount - CLAUDE.md's Execution
        // Layer: Third Correction fixed ShareCount from `long` to `decimal(18,9)` specifically
        // so Adjuster's own precisely-computed fractional quantity survives intact to this
        // point, with zero drift from an independent recompute against a live quote that could
        // differ from the price Adjuster actually sized against. Only the limit PRICE above is
        // still live; no cast is needed - ShareCount is decimal-native now.
        var baseQuantity = allocation.ShareCount;

        // Sentiment Adjuster Multiplier Decision (CLAUDE.md 2026-07-24) - fire-time,
        // bidirectional sizing adjustment. Deliberately called here, not inside
        // PreFireGateService (a pure pass/fail gate - PreFireGateResult{Passed, FailedGate,
        // Reason} - with no quantity or AdjusterAllocation involvement anywhere in it, despite
        // the decision's own text saying otherwise) and not inside AdjusterSizingService (that
        // computes ShareCount at scan time; sentiment must reflect conditions at the moment of
        // fire, which can be minutes to hours later). Already clamped to [MultiplierFloor,
        // MultiplierCeiling] internally by SentimentMultiplierService - never re-clamped here.
        var sentimentMultiplier = await _sentimentMultiplierService.GetMultiplierAsync(symbol, cancellationToken);

        // CLAUDE.md's Adjuster: Multi-Signal Multiplier Combination - Weighted-Deviation-Sum
        // Model (2026-07-26) - replaces straight multiplicative stacking (Kelly x insider x
        // sentiment x options-flow). insiderMultiplier and optionsFlowMultiplier are both
        // resolved at scan time and persisted on the allocation (AdjusterScanService -
        // AdjusterSizingService no longer multiplies insider into ShareCount, and
        // OptionsFlowMultiplierApplied never participated in ShareCount at all - see those
        // classes' own doc comments); sentimentMultiplier is only ever known live, here, at
        // fire time. This is the one point all three raw multipliers are known simultaneously,
        // so it is the one place they can genuinely be combined via a weighted deviation SUM
        // (not a further multiplication, which would just reintroduce the unbounded-compounding
        // problem this decision exists to remove). No signal's own raw-multiplier computation is
        // touched by this - only how they combine. allocation.OptionsFlowMultiplierApplied is
        // read directly, not re-fetched or recomputed - AdjusterScanService already did that
        // work once, at scan time (CboeOptionsChainClient -> OptionsFlowMultiplierService), and
        // this call site's whole job is to consume that persisted value, same as it already
        // does for InsiderMultiplierApplied.
        var combinedMultiplier = MultiSignalMultiplierCombiner.Combine(new[]
        {
            new WeightedMultiplier(_multiSignalWeights.InsiderWeight, allocation.InsiderMultiplierApplied),
            new WeightedMultiplier(_multiSignalWeights.SentimentWeight, sentimentMultiplier),
            new WeightedMultiplier(_multiSignalWeights.OptionsWeight, allocation.OptionsFlowMultiplierApplied)
        });

        var rawQuantity = baseQuantity * combinedMultiplier;

        _logger.LogInformation(
            "[ORDER-FIRING]: {Symbol}: base quantity {BaseQuantity} x combined multiplier {CombinedMultiplier} " +
            "(insider={InsiderMultiplier} x weight={InsiderWeight}, sentiment={SentimentMultiplier} x weight={SentimentWeight}, " +
            "optionsFlow={OptionsFlowMultiplier} x weight={OptionsWeight}) = {AdjustedQuantity}",
            symbol, baseQuantity, combinedMultiplier, allocation.InsiderMultiplierApplied, _multiSignalWeights.InsiderWeight,
            sentimentMultiplier, _multiSignalWeights.SentimentWeight, allocation.OptionsFlowMultiplierApplied,
            _multiSignalWeights.OptionsWeight, rawQuantity);

        var clientOrderId = $"drps-{gateScore.Id}-{allocation.Id}-{attempt}";

        // c. Branch on the (now combined-multiplier-adjusted) quantity.
        if (rawQuantity >= 1m)
        {
            var wholeQuantity = rawQuantity - (rawQuantity % 1m);
            var remainder = rawQuantity - wholeQuantity;

            if (remainder > 0m)
            {
                _logger.LogInformation(
                    "[ORDER-FIRING]: {Symbol}: flooring {RawQuantity} to {WholeQuantity} for the IOC order - " +
                    "discarding fractional remainder {Remainder}",
                    symbol, rawQuantity, wholeQuantity, remainder);
            }

            var request = new AlpacaOrderRequest
            {
                Symbol = symbol,
                Quantity = wholeQuantity,
                Side = "buy",
                Type = "limit",
                TimeInForce = "ioc",
                LimitPrice = limitPrice,
                ClientOrderId = clientOrderId
            };

            var order = await PlaceWithRetryClassificationAsync(request, cancellationToken);
            var iocResult = ToResult(order, request, remainder > 0m ? remainder : null, baseQuantity, sentimentMultiplier, combinedMultiplier);
            await NotifyOutcomeAsync("OPEN", symbol, iocResult, cancellationToken);
            return iocResult;
        }

        if (rawQuantity > 0m)
        {
            var request = new AlpacaOrderRequest
            {
                Symbol = symbol,
                Quantity = rawQuantity,
                Side = "buy",
                Type = "limit",
                TimeInForce = "day",
                LimitPrice = limitPrice,
                ClientOrderId = clientOrderId
            };

            var fractionalResult = await FireFractionalOrderAsync(request, cancellationToken, baseQuantity, sentimentMultiplier, combinedMultiplier);
            await NotifyOutcomeAsync("OPEN", symbol, fractionalResult, cancellationToken);
            return fractionalResult;
        }

        // Degenerate/defensive only - a genuinely zero ShareCount is already caught by the
        // ShareCapDeficient check above (AdjusterSizingService sets ShareCapDeficient = true
        // exactly when ShareCount == 0m), and ShareCount is never negative, so this should
        // never actually trigger.
        _logger.LogWarning("[ORDER-FIRING]: {Symbol}: computed quantity {RawQuantity} is not positive, skipping", symbol, rawQuantity);
        return new OrderFiringResult
        {
            Outcome = OrderFiringOutcome.SkippedZeroQuantity,
            Reason = $"Computed quantity {rawQuantity} is not positive"
        };
    }

    // Close-side counterpart to FireAsync - fires a sell order for an already-open Position's
    // full held quantity. No AdjusterAllocation is involved at all: a close needs no sizing
    // decision, just "sell what's currently held."
    //
    // Quantity source, confirmed directly from Position.cs rather than assumed: the entity has
    // no separate CurrentQuantity/RemainingQuantity field, and no partial-close mechanism
    // exists anywhere in this codebase (ManualPositionEntryService.ClosePositionAsync is the
    // only writer of ExitQuantity/ExitDate, and it closes a position in one shot - Position is
    // either fully open, with EntryQuantity as the only quantity ever set, or fully closed).
    // EntryQuantity is therefore genuinely the current held quantity, not a stale snapshot of
    // it - there is no other value it could have drifted from.
    //
    // `attempt` mirrors FireAsync's own parameter, but the client_order_id format is
    // deliberately different ("drps-close-{PositionId}-{attempt}", not
    // "drps-{GateScoreId}-{AllocationId}-{attempt}") - Position.GateScoreId/AdjusterAllocationId
    // reference the OPENING decision, and reusing those numbers here would risk colliding with
    // that original buy order's own client_order_id, corrupting Alpaca's duplicate-order
    // protection and this service's own ambiguous-failure status lookup for both orders.
    public async Task<OrderFiringResult> FireCloseAsync(Position position, CancellationToken cancellationToken, int attempt = 1)
    {
        // Sentiment Adjuster Multiplier Decision (CLAUDE.md 2026-07-24): deliberately never
        // called here - no AdjusterAllocation/sizing decision exists for a close, EntryQuantity
        // is sold in full, and sentiment-as-sizing-adjustment has nothing to adjust.
        var symbol = position.Ticker;

        // a. Same pre-fire gate obligation as FireAsync, but the close-side evaluation method -
        // see PreFireGateService.EvaluateCloseAsync's own doc comment for why kill-switch and
        // cash-floor are deliberately never evaluated for a close.
        var gateResult = await _preFireGateService.EvaluateCloseAsync(
            new PreFireCloseRequest { Symbol = symbol }, cancellationToken);

        if (!gateResult.Passed)
        {
            _logger.LogWarning(
                "[ORDER-FIRING]: {Symbol}: close not fired - rejected by pre-fire gate {FailedGate} ({Reason})",
                symbol, gateResult.FailedGate, gateResult.Reason);
            return new OrderFiringResult
            {
                Outcome = OrderFiringOutcome.RejectedByPreFireGate,
                Reason = $"{gateResult.FailedGate}: {gateResult.Reason}"
            };
        }

        // b. Marketable-limit price from a live quote - bid minus 0.25%, the mirror image of
        // FireAsync's ask-plus-0.25% (same uniform buffer/mechanism, per CLAUDE.md's Second
        // Design Decision Addendum). A sell limit must sit at or below the current bid to be
        // marketable; subtracting the buffer guarantees it crosses immediately against
        // existing bids rather than resting unfilled above them.
        AlpacaQuoteResult quoteResult;
        try
        {
            quoteResult = await _tradingClient.GetLatestQuoteAsync(symbol, cancellationToken);
        }
        catch (SymbolNotFoundException ex)
        {
            _logger.LogWarning("[ORDER-FIRING]: {Symbol}: quote fetch failed - {Message}", symbol, ex.Message);
            return new OrderFiringResult { Outcome = OrderFiringOutcome.QuoteFetchFailed, Reason = ex.Message };
        }

        if (!quoteResult.Success || quoteResult.Bid is null)
        {
            _logger.LogWarning("[ORDER-FIRING]: {Symbol}: quote fetch failed - {ErrorMessage}", symbol, quoteResult.ErrorMessage);
            return new OrderFiringResult { Outcome = OrderFiringOutcome.QuoteFetchFailed, Reason = quoteResult.ErrorMessage };
        }

        // Rounded DOWN to the nearest cent, never up - symmetric to FireAsync's ceiling above: a
        // sell limit rounded up risks landing above the bid, no longer marketable. Explicit
        // Math.Floor, not Math.Round (default MidpointRounding.ToEven is wrong in this
        // direction). Deliberately whole-cent only - sub-$1.00 sub-penny tick sizing (SEC Rule
        // 612) is out of scope for this fix, same as FireAsync's own ceiling above.
        var limitPrice = Math.Floor(quoteResult.Bid.Value * (1m - MarketableLimitBuffer) * 100m) / 100m;

        var rawQuantity = position.EntryQuantity;
        var clientOrderId = $"drps-close-{position.Id}-{attempt}";

        // c. Same IOC/Day-TIF branching as FireAsync's own quantity branch (CLAUDE.md's Second
        // Decision Addendum) - duplicated deliberately rather than extracted into a shared
        // helper, since sharing cleanly would require changing FireAsync's own body and this
        // task's scope explicitly excludes modifying FireAsync. PlaceWithRetryClassificationAsync/
        // FireFractionalOrderAsync/ToResult below are already side-agnostic private helpers, so
        // only this branch itself is duplicated, not the retry/fractional-order machinery.
        if (rawQuantity >= 1m)
        {
            var wholeQuantity = rawQuantity - (rawQuantity % 1m);
            var remainder = rawQuantity - wholeQuantity;

            if (remainder > 0m)
            {
                _logger.LogInformation(
                    "[ORDER-FIRING]: {Symbol}: flooring {RawQuantity} to {WholeQuantity} for the close IOC order - " +
                    "discarding fractional remainder {Remainder}",
                    symbol, rawQuantity, wholeQuantity, remainder);
            }

            var request = new AlpacaOrderRequest
            {
                Symbol = symbol,
                Quantity = wholeQuantity,
                Side = "sell",
                Type = "limit",
                TimeInForce = "ioc",
                LimitPrice = limitPrice,
                ClientOrderId = clientOrderId
            };

            var order = await PlaceWithRetryClassificationAsync(request, cancellationToken);
            var iocResult = ToResult(order, request, remainder > 0m ? remainder : null);
            await NotifyOutcomeAsync("CLOSE", symbol, iocResult, cancellationToken);
            return iocResult;
        }

        if (rawQuantity > 0m)
        {
            var request = new AlpacaOrderRequest
            {
                Symbol = symbol,
                Quantity = rawQuantity,
                Side = "sell",
                Type = "limit",
                TimeInForce = "day",
                LimitPrice = limitPrice,
                ClientOrderId = clientOrderId
            };

            var fractionalResult = await FireFractionalOrderAsync(request, cancellationToken);
            await NotifyOutcomeAsync("CLOSE", symbol, fractionalResult, cancellationToken);
            return fractionalResult;
        }

        // Degenerate/defensive only - EntryQuantity is set once at position-open time
        // (ManualPositionEntryService.OpenPositionAsync) and is never negative or zero in
        // practice, but this mirrors FireAsync's own defensive floor rather than assuming.
        _logger.LogWarning("[ORDER-FIRING]: {Symbol}: computed close quantity {RawQuantity} is not positive, skipping", symbol, rawQuantity);
        return new OrderFiringResult
        {
            Outcome = OrderFiringOutcome.SkippedZeroQuantity,
            Reason = $"Computed quantity {rawQuantity} is not positive"
        };
    }

    // baseQuantity/sentimentMultiplier/combinedMultiplier default to null - FireCloseAsync's own
    // call site (unchanged, per this task's scope) omits them, since a close has neither concept.
    private async Task<OrderFiringResult> FireFractionalOrderAsync(
        AlpacaOrderRequest request, CancellationToken cancellationToken,
        decimal? baseQuantity = null, decimal? sentimentMultiplier = null, decimal? combinedMultiplier = null)
    {
        var order = await PlaceWithRetryClassificationAsync(request, cancellationToken);
        var result = ToResult(order, request, discardedRemainder: null, baseQuantity, sentimentMultiplier, combinedMultiplier);

        if (result.Outcome != OrderFiringOutcome.Fired)
        {
            return result;
        }

        // Sub-1-share Day order - deliberately not fill-confirmation polling: a single
        // scheduled follow-up (wait once, check once, cancel if still unfilled), per this
        // task's explicit scope.
        await _delay(FractionalOrderCancelDelay, cancellationToken);

        var statusCheck = await _tradingClient.GetOrderByClientOrderIdAsync(request.ClientOrderId, cancellationToken);
        if (!statusCheck.Success)
        {
            _logger.LogWarning(
                "[ORDER-FIRING]: {ClientOrderId}: 5-minute status check failed ({ErrorMessage}) - cannot confirm " +
                "fill state, leaving the order as-is for manual review",
                request.ClientOrderId, statusCheck.ErrorMessage);
            return result;
        }

        if (!string.Equals(statusCheck.Status, "filled", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "[ORDER-FIRING]: {ClientOrderId}: unfilled after 5 minutes (status={Status}), canceling",
                request.ClientOrderId, statusCheck.Status);

            var cancelResult = await _tradingClient.CancelOrderAsync(statusCheck.OrderId!, cancellationToken);
            if (!cancelResult.Success)
            {
                _logger.LogWarning(
                    "[ORDER-FIRING]: {ClientOrderId}: cancel request failed - {ErrorMessage}",
                    request.ClientOrderId, cancelResult.ErrorMessage);
            }
        }

        return result;
    }

    // e. Retry classification: a clean 4xx is terminal, no retry. A timeout/5xx is ambiguous -
    // check order status by client_order_id before retrying; if found, treat as success; if
    // not, retry exactly once with the SAME client_order_id (never a new one, and never more
    // than this one retry).
    private async Task<AlpacaOrderResult> PlaceWithRetryClassificationAsync(AlpacaOrderRequest request, CancellationToken cancellationToken)
    {
        var firstAttempt = await _tradingClient.PlaceOrderAsync(request, cancellationToken);
        if (firstAttempt.Success)
        {
            return firstAttempt;
        }

        if (!firstAttempt.IsAmbiguousFailure)
        {
            _logger.LogWarning(
                "[ORDER-FIRING]: {ClientOrderId}: order rejected terminally - {ErrorMessage}",
                request.ClientOrderId, firstAttempt.ErrorMessage);
            return firstAttempt;
        }

        _logger.LogWarning(
            "[ORDER-FIRING]: {ClientOrderId}: ambiguous failure ({ErrorMessage}) - checking order status before retrying",
            request.ClientOrderId, firstAttempt.ErrorMessage);

        // A status check that itself fails to resolve (network error, not a confirmed 404) is
        // treated the same as "not found" below - either way, this is the one and only retry
        // this method will ever make, so there is no meaningfully different action to take.
        var statusCheck = await _tradingClient.GetOrderByClientOrderIdAsync(request.ClientOrderId, cancellationToken);
        if (statusCheck.Success)
        {
            _logger.LogInformation(
                "[ORDER-FIRING]: {ClientOrderId}: found via status check after ambiguous failure - treating as success",
                request.ClientOrderId);
            return statusCheck;
        }

        _logger.LogInformation(
            "[ORDER-FIRING]: {ClientOrderId}: not found after ambiguous failure - retrying once", request.ClientOrderId);
        return await _tradingClient.PlaceOrderAsync(request, cancellationToken);
    }

    // CLAUDE.md's Execution Layer: Ninth Design Decision - a genuine fire (Outcome.Fired) is one
    // of Pushover's wired trigger points, both directions (OPEN/CLOSE), both quantity branches
    // (IOC and the sub-1-share fractional/Day path).
    //
    // Extended 2026-07-31 (retry-ambiguity audit) to also notify on Outcome.AmbiguousUnresolved -
    // the case where the original PlaceOrderAsync call AND its one allowed retry both came back
    // ambiguous (timeout/5xx), so this service genuinely cannot say whether the order fired or
    // not. Before this, a human relying on Pushover alone (not tailing/grepping logs) saw
    // nothing when this happened - the only trace was a log line, and PlaceWithRetryClassificationAsync
    // itself logs nothing after the final retry's own result comes back (see that method's own
    // comments). This is exactly the "cannot confirm either way, needs manual review" case the
    // Ninth Design Decision's alerting philosophy was built for.
    //
    // Also persists an AmbiguousFireSkip row on that same outcome (2026-07-31 audit, Gap 1) - a
    // second, independent side effect of the identical condition, not something a "NotifyOutcome"
    // name fully captures on its own, but kept in this one method rather than a second pass over
    // the same switch: both effects fire from the exact same event, for both FireAsync and
    // FireCloseAsync, so there is exactly one place that decides "this outcome happened."
    //
    // Every other outcome (pre-fire-gate rejection, zero-quantity skip, quote-fetch failure,
    // a clean broker rejection) is already log-only elsewhere in this class and stays that way -
    // those are all clean, unambiguous outcomes with nothing for a human to reconcile against
    // Alpaca by hand, and nothing for a future fire attempt to guard against either.
    private async Task NotifyOutcomeAsync(string direction, string symbol, OrderFiringResult result, CancellationToken cancellationToken)
    {
        switch (result.Outcome)
        {
            case OrderFiringOutcome.Fired:
                await _pushoverNotificationService.SendAsync(
                    $"DRPS: {direction} order FIRED - {symbol} qty={result.FiredQuantity} orderId={result.OrderId} " +
                    $"clientOrderId={result.ClientOrderId}",
                    cancellationToken);
                return;

            case OrderFiringOutcome.AmbiguousUnresolved:
                // ClientOrderId and Reason are always populated for this outcome - ToResult sets
                // both on every non-Fired path it returns (see that method's own doc comment).
                // The explicit "grep" instruction matches how the audit itself found this case:
                // there is no single dedicated log line for AmbiguousUnresolved specifically, but
                // OrchestrationWorker/AtrRatchetMonitorWorker's own "{Ticker}: {OPEN|CLOSE} not
                // fired - {Outcome} ({Reason})" warning renders the literal string
                // "AmbiguousUnresolved", which is what a search should target.
                await _pushoverNotificationService.SendAsync(
                    $"DRPS: {direction} order AMBIGUOUS/UNRESOLVED - {symbol} clientOrderId={result.ClientOrderId} " +
                    $"reason={result.Reason} - could not confirm fill state with Alpaca after retry, manual check " +
                    "required. Search logs for \"AmbiguousUnresolved\" or this clientOrderId.",
                    cancellationToken);

                // CLAUDE.md's 2026-07-31 audit, Gap 1 - recorded for both directions (see
                // AmbiguousFireSkip's own doc comment for why only the OPEN path actually
                // consumes one). A fresh row every time, never reusing/updating an older
                // already-consumed one - each AmbiguousUnresolved occurrence is its own event.
                _dbContext.AmbiguousFireSkips.Add(new AmbiguousFireSkip
                {
                    Ticker = symbol,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                await _dbContext.SaveChangesAsync(cancellationToken);
                return;

            default:
                return;
        }
    }

    // baseQuantity/sentimentMultiplier/combinedMultiplier default to null - FireCloseAsync's own
    // call sites (unchanged, per this task's scope) never pass them, since a close has neither
    // concept (see FireCloseAsync's own top-of-method comment).
    private static OrderFiringResult ToResult(
        AlpacaOrderResult order, AlpacaOrderRequest request, decimal? discardedRemainder,
        decimal? baseQuantity = null, decimal? sentimentMultiplier = null, decimal? combinedMultiplier = null)
    {
        if (order.Success)
        {
            return new OrderFiringResult
            {
                Outcome = OrderFiringOutcome.Fired,
                OrderId = order.OrderId,
                ClientOrderId = order.ClientOrderId ?? request.ClientOrderId,
                FiredQuantity = request.Quantity,
                DiscardedFractionalRemainder = discardedRemainder,
                BaseQuantity = baseQuantity,
                SentimentMultiplier = sentimentMultiplier,
                CombinedMultiplier = combinedMultiplier
            };
        }

        var outcome = order.IsAmbiguousFailure ? OrderFiringOutcome.AmbiguousUnresolved : OrderFiringOutcome.RejectedByBroker;
        return new OrderFiringResult { Outcome = outcome, Reason = order.ErrorMessage, ClientOrderId = request.ClientOrderId };
    }
}
