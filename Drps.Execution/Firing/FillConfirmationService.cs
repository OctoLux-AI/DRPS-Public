using Drps.Execution.Alpaca;
using Drps.Ledger;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Drps.Execution.Firing;

/// <summary>
/// The piece named as a still-open gap by CLAUDE.md's Execution Layer: Fourth Design
/// Decision: OrderFiringService.FireAsync/FireCloseAsync place orders but never wait for them
/// to fill or write the result anywhere. This class closes that gap - given a fired order's
/// client_order_id and the context needed for the write-back, it polls
/// IAlpacaTradingClient.GetOrderByClientOrderIdAsync until a terminal state or the caller's
/// maxWait elapses, then calls LedgerPositionWriter (the shared, audited Position write path)
/// to record what actually happened.
///
/// Deliberately standalone - this task does NOT wire it into OrderFiringService and does NOT
/// add any BackgroundService/orchestrator loop. Something else (a later task) owns deciding
/// when to call this, with what maxWait, and what to do with the result.
///
/// maxWait is supplied by the caller rather than derived from the order's own time-in-force
/// here: whatever fired the order already knows whether it requested "ioc" or "day" TIF (see
/// OrderFiringService's own quantity-branch comments), so this class stays TIF-agnostic and
/// simply polls for up to however long the caller says "TIF + a small buffer" amounts to.
/// </summary>
public class FillConfirmationService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);

    // N=2 in EntryPrice + N x EntryAtr - locked by CLAUDE.md's Execution Layer: Addendum (TP
    // Multiplier Locked), tighter than the Chandelier stop's own 3.0x starting multiplier.
    private const decimal TpTargetAtrMultiple = 2m;

    private readonly IAlpacaTradingClient _tradingClient;
    private readonly LedgerPositionWriter _positionWriter;
    private readonly ILogger<FillConfirmationService> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _nowProvider;
    private readonly TimeSpan _pollInterval;

    public FillConfirmationService(
        IAlpacaTradingClient tradingClient,
        LedgerPositionWriter positionWriter,
        ILogger<FillConfirmationService> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTime>? nowProvider = null,
        TimeSpan? pollInterval = null)
    {
        _tradingClient = tradingClient;
        _positionWriter = positionWriter;
        _logger = logger;
        // Injectable so tests can make the poll loop resolve instantly - same seam pattern as
        // OrderFiringService's injectable delay.
        _delay = delay ?? Task.Delay;
        // Injectable so tests don't depend on the real wall clock - same seam pattern as
        // Gate's own as-of clock injection (CLAUDE.md's Gate: As-Of Clock-Injection Pattern).
        _nowProvider = nowProvider ?? (() => DateTime.Now);
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <summary>
    /// Confirms a fired open order and, on a real fill, records it as a new Position via
    /// LedgerPositionWriter.OpenPositionAsync. EntryAtr/TpTargetPrice are seeded from
    /// gateScore.AtrValue - the ATR value Gate actually saw when it produced this
    /// recommendation, not a value re-fetched fresh at fill-confirmation time.
    /// </summary>
    public async Task<FillConfirmationResult> ConfirmOpenAsync(
        string clientOrderId,
        GateScore gateScore,
        AdjusterAllocation allocation,
        TimeSpan maxWait,
        CancellationToken cancellationToken)
    {
        var poll = await PollUntilTerminalAsync(clientOrderId, maxWait, cancellationToken);

        var earlyResult = ToEarlyResult(clientOrderId, poll);
        if (earlyResult is not null)
        {
            return earlyResult;
        }

        var order = poll.Order!;
        var entryAtr = gateScore.AtrValue;
        var tpTargetPrice = order.FilledAveragePrice!.Value + (TpTargetAtrMultiple * entryAtr);
        // High-water mark starts at the real entry fill price - AtrRatchetMonitorWorker only
        // ever raises it from here (CLAUDE.md's Execution Layer: Third Design Decision).
        var highWaterMark = order.FilledAveragePrice!.Value;

        try
        {
            var position = await _positionWriter.OpenPositionAsync(
                gateScore.Id,
                allocation.Id,
                gateScore.Ticker,
                _nowProvider(),
                order.FilledAveragePrice!.Value,
                order.FilledQuantity!.Value,
                cancellationToken,
                // PositionActionOrigin.Automated is hardcoded - CLAUDE.md's Execution Layer:
                // Tenth Design Decision. This class's entire reason to exist is being the
                // automated path, so it is the only origin value it will ever pass.
                PositionActionOrigin.Automated,
                entryAtr: entryAtr,
                tpTargetPrice: tpTargetPrice,
                highWaterMark: highWaterMark);

            _logger.LogInformation(
                "[FILL-CONFIRMATION]: {ClientOrderId}: recorded open Position {PositionId} for {Ticker} " +
                "({Quantity} @ {Price}, EntryAtr={EntryAtr}, TpTargetPrice={TpTargetPrice}, HighWaterMark={HighWaterMark})",
                clientOrderId, position.Id, gateScore.Ticker, order.FilledQuantity, order.FilledAveragePrice,
                entryAtr, tpTargetPrice, highWaterMark);

            return new FillConfirmationResult
            {
                Outcome = FillConfirmationOutcome.Recorded,
                Position = position,
                FilledQuantity = order.FilledQuantity,
                FilledAveragePrice = order.FilledAveragePrice
            };
        }
        catch (DuplicateOpenPositionException ex)
        {
            // Something else already wrote an open position for this ticker - a real anomaly,
            // logged clearly rather than crashing unhandled or being silently swallowed
            // (CLAUDE.md's Execution Layer: Fourth Design Decision).
            _logger.LogError(
                ex,
                "[FILL-CONFIRMATION]: {ClientOrderId}: LedgerWriteAnomaly - {Message}",
                clientOrderId, ex.Message);

            return new FillConfirmationResult
            {
                Outcome = FillConfirmationOutcome.LedgerWriteAnomaly,
                Reason = ex.Message,
                FilledQuantity = order.FilledQuantity,
                FilledAveragePrice = order.FilledAveragePrice
            };
        }
    }

    /// <summary>
    /// Confirms a fired close order and, on a real fill, records it against the given, already-
    /// open Position via LedgerPositionWriter.ClosePositionAsync. exitReason is supplied by the
    /// caller - whatever decided to close this position (composite degradation, ATR ratchet,
    /// plateau deactivation, sector displacement) already knows why, and Position itself
    /// carries no "pending exit reason" until the close is actually recorded.
    ///
    /// tpProgressAtExit is supplied only by AtrRatchetMonitorWorker's breach path (the raw,
    /// uncapped ratchet progress at the moment of breach) - every other caller/ExitReason
    /// leaves it null, per CLAUDE.md's Execution Layer: Third Design Decision's "grading
    /// distinction, without forking behavior."
    /// </summary>
    public async Task<FillConfirmationResult> ConfirmCloseAsync(
        string clientOrderId,
        Position position,
        PositionExitReason exitReason,
        TimeSpan maxWait,
        CancellationToken cancellationToken,
        decimal? tpProgressAtExit = null)
    {
        var poll = await PollUntilTerminalAsync(clientOrderId, maxWait, cancellationToken);

        var earlyResult = ToEarlyResult(clientOrderId, poll);
        if (earlyResult is not null)
        {
            return earlyResult;
        }

        var order = poll.Order!;

        try
        {
            await _positionWriter.ClosePositionAsync(
                position.Id,
                _nowProvider(),
                order.FilledAveragePrice!.Value,
                order.FilledQuantity!.Value,
                exitReason,
                cancellationToken,
                // PositionActionOrigin.Automated is hardcoded, same reasoning as
                // ConfirmOpenAsync above.
                PositionActionOrigin.Automated,
                tpProgressAtExit: tpProgressAtExit);

            _logger.LogInformation(
                "[FILL-CONFIRMATION]: {ClientOrderId}: recorded close of Position {PositionId} for {Ticker} " +
                "({Quantity} @ {Price}, ExitReason={ExitReason})",
                clientOrderId, position.Id, position.Ticker, order.FilledQuantity, order.FilledAveragePrice, exitReason);

            return new FillConfirmationResult
            {
                Outcome = FillConfirmationOutcome.Recorded,
                FilledQuantity = order.FilledQuantity,
                FilledAveragePrice = order.FilledAveragePrice
            };
        }
        catch (PositionAlreadyModifiedException ex)
        {
            // Another writer already closed (or otherwise modified) this Position row - a real
            // anomaly, logged clearly rather than crashing unhandled or being silently
            // swallowed (CLAUDE.md's Execution Layer: Fourth Design Decision).
            _logger.LogError(
                ex,
                "[FILL-CONFIRMATION]: {ClientOrderId}: LedgerWriteAnomaly - {Message}",
                clientOrderId, ex.Message);

            return new FillConfirmationResult
            {
                Outcome = FillConfirmationOutcome.LedgerWriteAnomaly,
                Reason = ex.Message,
                FilledQuantity = order.FilledQuantity,
                FilledAveragePrice = order.FilledAveragePrice
            };
        }
    }

    // Shared early-exit handling for every PollOutcome that does NOT lead to a Ledger write -
    // returns null only for PollOutcome.HasFill, signaling the caller should proceed to write.
    private FillConfirmationResult? ToEarlyResult(string clientOrderId, PollResult poll)
    {
        switch (poll.Outcome)
        {
            case PollOutcome.TimedOut:
                _logger.LogCritical(
                    "[FILL-CONFIRMATION]: {ClientOrderId}: still non-terminal after the caller's maxWait - " +
                    "stopping poll, this is now a manual reconciliation case",
                    clientOrderId);
                return new FillConfirmationResult
                {
                    Outcome = FillConfirmationOutcome.TimedOut,
                    Reason = "Order did not reach a terminal state within the caller's maxWait."
                };

            case PollOutcome.NoFill:
                _logger.LogInformation(
                    "[FILL-CONFIRMATION]: {ClientOrderId}: reached terminal status {Status} with nothing filled - " +
                    "nothing to record",
                    clientOrderId, poll.TerminalStatus);
                return new FillConfirmationResult
                {
                    Outcome = FillConfirmationOutcome.NoFillRecorded,
                    Reason = poll.TerminalStatus
                };

            case PollOutcome.AnomalousFillData:
                _logger.LogCritical(
                    "[FILL-CONFIRMATION]: {ClientOrderId}: status {Status} indicated a fill but FilledQuantity/" +
                    "FilledAveragePrice were missing or non-positive ({Quantity}/{Price}) - not writing to Ledger, " +
                    "manual reconciliation required",
                    clientOrderId, poll.TerminalStatus, poll.Order?.FilledQuantity, poll.Order?.FilledAveragePrice);
                return new FillConfirmationResult
                {
                    Outcome = FillConfirmationOutcome.AnomalousFillData,
                    Reason = $"Status '{poll.TerminalStatus}' indicated a fill but fill data was missing or non-positive."
                };

            default:
                // PollOutcome.HasFill - not an early result, caller proceeds to write.
                return null;
        }
    }

    private async Task<PollResult> PollUntilTerminalAsync(
        string clientOrderId, TimeSpan maxWait, CancellationToken cancellationToken)
    {
        var elapsed = TimeSpan.Zero;

        while (true)
        {
            var order = await _tradingClient.GetOrderByClientOrderIdAsync(clientOrderId, cancellationToken);

            if (order.Success)
            {
                var status = order.Status?.ToLowerInvariant();
                var hasPositiveFill = order.FilledQuantity is > 0m && order.FilledAveragePrice is > 0m;

                if (status == "filled")
                {
                    return hasPositiveFill
                        ? new PollResult(PollOutcome.HasFill, order, status)
                        : new PollResult(PollOutcome.AnomalousFillData, order, status);
                }

                if (status is "canceled" or "expired")
                {
                    // Accepted as-is, not chased (CLAUDE.md's Execution Layer: Fourth Design
                    // Decision) - a partial fill that then settles into canceled/expired still
                    // gets recorded for whatever quantity actually filled.
                    return hasPositiveFill
                        ? new PollResult(PollOutcome.HasFill, order, status)
                        : new PollResult(PollOutcome.NoFill, order, status);
                }

                if (status == "rejected")
                {
                    // A rejected order can never carry a real fill - treated as NoFill
                    // regardless of whatever FilledQuantity/FilledAveragePrice happen to read.
                    return new PollResult(PollOutcome.NoFill, order, status);
                }

                // "accepted"/"new"/"pending_new"/"partially_filled"/etc - still active, not
                // terminal yet. Keep polling.
            }

            if (elapsed >= maxWait)
            {
                return new PollResult(PollOutcome.TimedOut, null, null);
            }

            await _delay(_pollInterval, cancellationToken);
            elapsed += _pollInterval;
        }
    }

    private enum PollOutcome
    {
        HasFill,
        NoFill,
        TimedOut,
        AnomalousFillData
    }

    private readonly record struct PollResult(PollOutcome Outcome, AlpacaOrderResult? Order, string? TerminalStatus);
}
