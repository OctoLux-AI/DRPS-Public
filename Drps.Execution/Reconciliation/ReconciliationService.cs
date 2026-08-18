using Drps.Execution.Alpaca;
using Drps.Execution.Notifications;
using Drps.Ingestion.Persistence;
using Drps.Ledger;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Drps.Execution.Reconciliation;

/// <summary>
/// Implements CLAUDE.md's Execution Layer: Fifth Design Decision (Ledger/Alpaca Reconciliation)
/// - a two-way set comparison between Alpaca's real open positions and Drps.Ledger's believed
/// open Position rows, closing the last piece of that design block's scope. Pure, testable
/// logic only: a single method something else (a not-yet-built scheduled worker, per that same
/// design block's "own periodic worker, its own cadence" note) will eventually call on a
/// schedule. No BackgroundService/hosting/scheduling anywhere in this class.
///
/// Alpaca's account is treated as unambiguous ground truth here, not "primary source with a
/// narrow known-exception" the way the OHLCV reconciliation model elsewhere in this codebase
/// treats Alpaca vs. Tiingo - it's the account literally holding the capital, not a data feed
/// that can misreport a print. Drps.Ledger is DRPS's own belief about what happened, and
/// beliefs can drift.
///
/// Three cases, per the locked design:
/// - Orphans (Alpaca minus Ledger) - a real Alpaca position with no matching open Ledger row.
///   Cannot be auto-healed (no EntryAtr/TpTargetPrice/GateScore link exists for it) - excluded
///   per-ticker via ExcludedTicker (OpenCandidateQuery already respects this table), never a
///   global halt. Idempotent: a ticker already on ExcludedTicker is never re-inserted, only
///   re-logged.
/// - Phantoms (Ledger minus Alpaca) - a Position Ledger believes open that Alpaca's account no
///   longer shows. Resolved by searching Alpaca's own closed-order history for that ticker via
///   ListOrdersBySymbolAsync - the same authoritative-confirmation pattern already trusted
///   elsewhere (OrderFiringService's ambiguous-failure handling), not a new heuristic. Only a
///   genuinely filled SELL order counts as evidence: a canceled/expired order proves nothing,
///   and a filled BUY order is the entry fill that opened this same position, not evidence it
///   closed - blindly accepting "any filled order for this symbol" would wrongly match that
///   entry order. If found, closes the position via LedgerPositionWriter (the shared, audited
///   write path) with PositionExitReason.ReconciliationHealed. If not, logs Critical and writes
///   nothing - never guesses.
/// - Intersection - both sides agree a position is open; quantity/price are compared with a
///   small explicit tolerance (decimal noise from Alpaca's own JSON parsing, not a real
///   disagreement). Outside tolerance logs a PositionReconciliationDiscrepancy row only - never
///   overwrites Position's own fields, per the single-writer invariant LedgerPositionWriter
///   already enforces.
/// </summary>
public class ReconciliationService
{
    // Placeholder tolerances, stated explicitly per this codebase's own numeric-guess
    // discipline (same category as BarReconciliationService's $0.02 absolute floor) - decimal
    // noise from Alpaca's own JSON parsing shouldn't produce a false-positive discrepancy.
    private const decimal QuantityTolerance = 0.000001m;
    private const decimal PriceTolerance = 0.01m;

    private readonly DrpsDbContext _dbContext;
    private readonly IAlpacaTradingClient _tradingClient;
    private readonly LedgerPositionWriter _positionWriter;
    private readonly IPushoverNotificationService _pushoverNotificationService;
    private readonly ILogger<ReconciliationService> _logger;
    private readonly Func<DateTime> _nowProvider;

    public ReconciliationService(
        DrpsDbContext dbContext,
        IAlpacaTradingClient tradingClient,
        LedgerPositionWriter positionWriter,
        IPushoverNotificationService pushoverNotificationService,
        ILogger<ReconciliationService> logger,
        Func<DateTime>? nowProvider = null)
    {
        _dbContext = dbContext;
        _tradingClient = tradingClient;
        _positionWriter = positionWriter;
        _pushoverNotificationService = pushoverNotificationService;
        _logger = logger;
        // Injectable so tests don't depend on the real wall clock - same seam pattern as
        // FillConfirmationService/Gate's own as-of clock injection.
        _nowProvider = nowProvider ?? (() => DateTime.Now);
    }

    public async Task<ReconciliationResult> RunAsync(CancellationToken cancellationToken)
    {
        var alpacaPositions = await _tradingClient.GetOpenPositionsAsync(cancellationToken);
        if (!alpacaPositions.Success)
        {
            _logger.LogCritical(
                "[RECONCILIATION]: failed to fetch Alpaca's open positions - {ErrorMessage} - reconciliation skipped this run",
                alpacaPositions.ErrorMessage);
            return new ReconciliationResult { Success = false, ErrorMessage = alpacaPositions.ErrorMessage };
        }

        var ledgerPositions = await _dbContext.Positions
            .Where(p => p.ExitDate == null)
            .ToListAsync(cancellationToken);

        var alpacaByTicker = alpacaPositions.Positions
            .ToDictionary(p => p.Symbol, StringComparer.OrdinalIgnoreCase);
        var ledgerTickers = ledgerPositions
            .Select(p => p.Ticker)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new ReconciliationResult();

        foreach (var alpacaPosition in alpacaPositions.Positions)
        {
            if (ledgerTickers.Contains(alpacaPosition.Symbol))
            {
                continue;
            }

            await HandleOrphanAsync(alpacaPosition, result, cancellationToken);
        }

        foreach (var ledgerPosition in ledgerPositions)
        {
            if (!alpacaByTicker.TryGetValue(ledgerPosition.Ticker, out var alpacaPosition))
            {
                await HandlePhantomAsync(ledgerPosition, result, cancellationToken);
                continue;
            }

            CheckIntersection(ledgerPosition, alpacaPosition, result);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return result;
    }

    private async Task HandleOrphanAsync(AlpacaPosition alpacaPosition, ReconciliationResult result, CancellationToken cancellationToken)
    {
        var ticker = alpacaPosition.Symbol;

        var alreadyExcluded = await _dbContext.ExcludedTickers
            .AnyAsync(e => e.Ticker == ticker, cancellationToken);

        if (alreadyExcluded)
        {
            _logger.LogCritical(
                "[RECONCILIATION]: {Ticker}: orphan Alpaca position (no matching open Ledger row) - already on " +
                "ExcludedTicker, not re-inserted",
                ticker);
            await _pushoverNotificationService.SendAsync(
                $"DRPS RECONCILIATION: {ticker}: orphan Alpaca position (no matching open Ledger row) - already on " +
                "ExcludedTicker, not re-inserted",
                cancellationToken);
            result.AlreadyKnownOrphans++;
            return;
        }

        _dbContext.ExcludedTickers.Add(new ExcludedTicker
        {
            Ticker = ticker,
            Reason = "Reconciliation: Alpaca holds an open position with no matching open Drps.Ledger Position row " +
                     "(Execution Layer: Fifth Design Decision) - cannot be auto-healed, excluded pending manual review.",
            CreatedDate = _nowProvider()
        });

        _logger.LogCritical(
            "[RECONCILIATION]: {Ticker}: NEW orphan Alpaca position detected (no matching open Ledger row) - " +
            "excluded from new automated action, manual resolution required",
            ticker);
        await _pushoverNotificationService.SendAsync(
            $"DRPS RECONCILIATION: {ticker}: NEW orphan Alpaca position detected (no matching open Ledger row) - " +
            "excluded from new automated action, manual resolution required",
            cancellationToken);
        result.NewOrphansDetected++;
    }

    private async Task HandlePhantomAsync(Position ledgerPosition, ReconciliationResult result, CancellationToken cancellationToken)
    {
        var ticker = ledgerPosition.Ticker;

        var ordersResult = await _tradingClient.ListOrdersBySymbolAsync(ticker, cancellationToken);
        if (!ordersResult.Success)
        {
            _logger.LogCritical(
                "[RECONCILIATION]: {Ticker}: phantom Position {PositionId} (Ledger believes open, Alpaca shows no " +
                "matching open position) - order-history fetch failed ({ErrorMessage}), cannot confirm a close, " +
                "leaving unresolved",
                ticker, ledgerPosition.Id, ordersResult.ErrorMessage);
            await _pushoverNotificationService.SendAsync(
                $"DRPS RECONCILIATION: {ticker}: phantom Position {ledgerPosition.Id} - order-history fetch failed " +
                $"({ordersResult.ErrorMessage}), cannot confirm a close, leaving unresolved",
                cancellationToken);
            result.PhantomsUnresolved++;
            return;
        }

        // Only a genuinely filled SELL order is evidence this position actually closed. A
        // filled BUY order also appears in this same history - it's the entry fill that opened
        // this position, not evidence it closed, and must not be mistaken for a closing order.
        // A canceled/expired order (also returned by this same status=closed history) proves
        // nothing either way. Among multiple qualifying closes (should not normally happen for
        // a single lifecycle, but not assumed impossible), the most recent by FilledAt wins.
        var closingFill = ordersResult.Orders
            .Where(o => string.Equals(o.Status, "filled", StringComparison.OrdinalIgnoreCase))
            .Where(o => string.Equals(o.Side, "sell", StringComparison.OrdinalIgnoreCase))
            .Where(o => o.FilledQuantity is > 0m && o.FilledAveragePrice is > 0m)
            .OrderByDescending(o => o.FilledAt ?? DateTimeOffset.MinValue)
            .Select(o => (AlpacaOrder?)o)
            .FirstOrDefault();

        if (closingFill is null)
        {
            _logger.LogCritical(
                "[RECONCILIATION]: {Ticker}: phantom Position {PositionId} (Ledger believes open, Alpaca shows no " +
                "matching open position) - no genuinely filled closing order found in order history, cannot " +
                "auto-heal, manual reconciliation required",
                ticker, ledgerPosition.Id);
            await _pushoverNotificationService.SendAsync(
                $"DRPS RECONCILIATION: {ticker}: phantom Position {ledgerPosition.Id} - no genuinely filled closing " +
                "order found, cannot auto-heal, manual reconciliation required",
                cancellationToken);
            result.PhantomsUnresolved++;
            return;
        }

        var order = closingFill.Value;

        try
        {
            await _positionWriter.ClosePositionAsync(
                ledgerPosition.Id,
                _nowProvider(),
                order.FilledAveragePrice!.Value,
                order.FilledQuantity!.Value,
                PositionExitReason.ReconciliationHealed,
                cancellationToken,
                // PositionActionOrigin.Automated - CLAUDE.md's Execution Layer: Tenth Design
                // Decision. Not itself named in that block's enumerated call sites, but this
                // class is wholly an automated Execution-layer component (no human decides or
                // types this close), so the same reasoning that hardcodes FillConfirmationService
                // to Automated applies here identically.
                PositionActionOrigin.Automated);

            _logger.LogCritical(
                "[RECONCILIATION]: {Ticker}: phantom Position {PositionId} auto-healed - closed via " +
                "ReconciliationHealed ({Quantity} @ {Price}, confirmed by order {OrderId})",
                ticker, ledgerPosition.Id, order.FilledQuantity, order.FilledAveragePrice, order.OrderId);
            await _pushoverNotificationService.SendAsync(
                $"DRPS RECONCILIATION: {ticker}: phantom Position {ledgerPosition.Id} auto-healed - closed via " +
                $"ReconciliationHealed ({order.FilledQuantity} @ {order.FilledAveragePrice}, confirmed by order " +
                $"{order.OrderId})",
                cancellationToken);
            result.PhantomsHealed++;
        }
        catch (PositionAlreadyModifiedException ex)
        {
            // Another writer already closed/modified this row before reconciliation could - a
            // real anomaly, logged clearly rather than crashing the whole run (same carve-out
            // FillConfirmationService already applies to this exact exception type).
            _logger.LogCritical(
                ex,
                "[RECONCILIATION]: {Ticker}: phantom Position {PositionId} - {Message}",
                ticker, ledgerPosition.Id, ex.Message);
            await _pushoverNotificationService.SendAsync(
                $"DRPS RECONCILIATION: {ticker}: phantom Position {ledgerPosition.Id} - {ex.Message}",
                cancellationToken);
            result.PhantomsUnresolved++;
        }
    }

    // Never overwrites Position's own fields on a mismatch - single-writer invariant
    // (LedgerPositionWriter is the only writer to Entry*/Exit* fields). Logging only.
    private void CheckIntersection(Position ledgerPosition, AlpacaPosition alpacaPosition, ReconciliationResult result)
    {
        var quantityDelta = Math.Abs(ledgerPosition.EntryQuantity - alpacaPosition.Quantity);
        var priceDelta = Math.Abs(ledgerPosition.EntryPrice - alpacaPosition.AverageEntryPrice);

        if (quantityDelta <= QuantityTolerance && priceDelta <= PriceTolerance)
        {
            return;
        }

        _dbContext.PositionReconciliationDiscrepancies.Add(new PositionReconciliationDiscrepancy
        {
            PositionId = ledgerPosition.Id,
            Ticker = ledgerPosition.Ticker,
            DetectedDate = _nowProvider(),
            LedgerQuantity = ledgerPosition.EntryQuantity,
            AlpacaQuantity = alpacaPosition.Quantity,
            LedgerPrice = ledgerPosition.EntryPrice,
            AlpacaAverageEntryPrice = alpacaPosition.AverageEntryPrice
        });

        _logger.LogCritical(
            "[RECONCILIATION]: {Ticker}: Position {PositionId} quantity/price mismatch vs Alpaca - " +
            "Ledger={LedgerQuantity}@{LedgerPrice} Alpaca={AlpacaQuantity}@{AlpacaAveragePrice} - logged, " +
            "Position row not modified",
            ledgerPosition.Ticker, ledgerPosition.Id, ledgerPosition.EntryQuantity, ledgerPosition.EntryPrice,
            alpacaPosition.Quantity, alpacaPosition.AverageEntryPrice);

        result.DiscrepanciesLogged++;
    }
}
