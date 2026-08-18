using Drps.Execution.Candidates;
using Drps.Execution.Firing;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Drps.Execution;

/// <summary>
/// The last piece of the Execution Layer's original four-part scope (pre-fire gates, fire
/// mechanism, ATR ratchet, fill confirmation, per CLAUDE.md's Execution Layer: Fourth Design
/// Decision) plus this task: wires CandidateOrchestrator's output to real order firing
/// (OrderFiringService) and fill confirmation (FillConfirmationService), on a flat polling
/// interval - a genuinely new BackgroundService, not a variant of the fixed-daily-time pattern
/// Drps.Gate/Drps.Ingestion use for their once-a-day batch scans (Orchestration needs to react
/// to intraday candidates promptly, not once at a scheduled time).
///
/// In-flight tracking (CLAUDE.md's Execution Layer: Sixth Design Decision) is delegated to an
/// injected IInFlightPositionTracker - a ticker is marked in-flight the moment its fire attempt
/// is scheduled and released only once fire+fill-confirmation (or a dry-run log-only) fully
/// resolves, however many poll cycles that spans. A candidate already in-flight is skipped,
/// never re-fired. The tracker is registered as a singleton in production so the not-yet-built
/// ATR ratchet monitor worker can share the exact same in-flight state, per CLAUDE.md's decision
/// that both workers must share one tracker to prevent racing to close the same position
/// independently.
///
/// Each candidate's fire-then-confirm sequence runs as its own independent, detached task -
/// deliberately NOT awaited by RunCycleAsync itself, since a fractional Day-TIF fill-confirmation
/// wait can take up to several minutes and must not block sibling candidates in the same cycle
/// or the next poll cycle's own candidate discovery. RunCycleAsync returns the spawned tasks
/// purely so tests can await them deterministically - production code (ExecuteAsync) does not
/// await them individually.
/// </summary>
public class OrchestrationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<OrchestrationSettings> _settings;
    private readonly ILogger<OrchestrationWorker> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IInFlightPositionTracker _inFlightTracker;

    public OrchestrationWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<OrchestrationSettings> settings,
        ILogger<OrchestrationWorker> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        IInFlightPositionTracker? inFlightTracker = null)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _logger = logger;
        // Injectable so tests can make the between-cycle wait resolve instantly - same seam
        // pattern as OrderFiringService/FillConfirmationService's own injectable delay.
        _delay = delay ?? Task.Delay;
        // Defaults to a private instance (e.g. for a test that doesn't care about sharing) -
        // production DI registers one singleton instance shared with the future ATR ratchet
        // monitor worker.
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
    /// Discovers this cycle's actionable candidates and spawns one detached task per candidate
    /// not already in-flight. Returns immediately once spawning (not processing) is done.
    /// </summary>
    public async Task<IReadOnlyList<Task>> RunCycleAsync(CancellationToken stoppingToken)
    {
        ActionableCandidates? candidates = null;

        // Candidate discovery gets the same fail-open-to-next-cycle treatment CLAUDE.md's
        // existing Worker precedent (Drps.Gate/Drps.Ingestion) already applies to its own
        // per-cycle work - a transient discovery-query failure must not crash this entire
        // BackgroundService any more than one candidate's own failure may (this task's own
        // explicit requirement, extended one level up to the discovery step itself).
        using (var scope = _scopeFactory.CreateScope())
        {
            try
            {
                var orchestrator = scope.ServiceProvider.GetRequiredService<CandidateOrchestrator>();
                candidates = await orchestrator.GetActionableCandidatesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ORCHESTRATION-WORKER]: candidate discovery failed this cycle - no candidates processed");
            }
        }

        if (candidates is null)
        {
            return Array.Empty<Task>();
        }

        var spawned = new List<Task>();

        foreach (var openCandidate in candidates.OpenActions)
        {
            var ticker = openCandidate.GateScore.Ticker;

            // Mark in-flight BEFORE starting the task - released only in that task's own
            // finally block, covering every outcome.
            if (!_inFlightTracker.TryMarkInFlight(ticker))
            {
                _logger.LogInformation(
                    "[ORCHESTRATION-WORKER]: {Ticker}: OPEN candidate skipped - already in-flight", ticker);
                continue;
            }

            spawned.Add(ProcessOpenCandidateAsync(openCandidate, stoppingToken));
        }

        foreach (var closeAction in candidates.CloseActions)
        {
            var ticker = closeAction.Position.Ticker;

            if (!_inFlightTracker.TryMarkInFlight(ticker))
            {
                _logger.LogInformation(
                    "[ORCHESTRATION-WORKER]: {Ticker}: CLOSE candidate skipped - already in-flight", ticker);
                continue;
            }

            spawned.Add(ProcessCloseCandidateAsync(closeAction, stoppingToken));
        }

        return spawned;
    }

    private async Task ProcessOpenCandidateAsync(OpenCandidate candidate, CancellationToken stoppingToken)
    {
        var ticker = candidate.GateScore.Ticker;

        try
        {
            var settings = _settings.Value;

            if (settings.IsDryRunEnabled)
            {
                // Dry-run: log exactly what would have fired, using only context already
                // available before ever calling FireAsync (a live quote is fetched inside
                // FireAsync itself, so it is deliberately not fabricated here).
                _logger.LogInformation(
                    "[ORCHESTRATION-WORKER]: DRY-RUN: would fire OPEN for {Ticker} (GateScoreId={GateScoreId}, " +
                    "AdjusterAllocationId={AllocationId}, ShareCount={ShareCount}, AllocationDollarAmount={AllocationDollarAmount})",
                    ticker, candidate.GateScore.Id, candidate.AdjusterAllocation.Id,
                    candidate.AdjusterAllocation.ShareCount, candidate.AdjusterAllocation.AllocationDollarAmount);
                return;
            }

            using var scope = _scopeFactory.CreateScope();

            // CLAUDE.md's 2026-07-31 retry-ambiguity audit, Gap 1 - checked here, not
            // OpenCandidateQuery, deliberately: this needs to both see the candidate AND write
            // (consume) the skip row in the same step, which OpenCandidateQuery's own read-only
            // contract ("no side effects, no writes") cannot do. If OpenCandidateQuery instead
            // filtered the ticker out entirely, nothing would ever consume the row and the skip
            // would become a permanent, silent exclusion - exactly the ExcludedTicker-shaped
            // behavior this mechanism is deliberately NOT meant to have. Scoped to the OPEN path
            // only: FireCloseAsync's own client_order_id ("drps-close-{PositionId}-{attempt}")
            // is keyed on the stable Position.Id, not a GateScoreId/AdjusterAllocationId pair a
            // Gate re-scan can supersede, so the double-fire window this guard exists to narrow
            // does not apply to closes - see AmbiguousFireSkip's own doc comment.
            var dbContext = scope.ServiceProvider.GetRequiredService<DrpsDbContext>();
            var pendingSkip = await dbContext.AmbiguousFireSkips
                .Where(s => s.Ticker == ticker && s.ConsumedAt == null)
                .OrderBy(s => s.CreatedAt)
                .FirstOrDefaultAsync(stoppingToken);

            if (pendingSkip is not null)
            {
                pendingSkip.ConsumedAt = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(stoppingToken);

                _logger.LogWarning(
                    "[ORCHESTRATION-WORKER]: {Ticker}: OPEN skipped once (not fired) - unconsumed " +
                    "AmbiguousFireSkip row (Id={SkipId}, CreatedAt={CreatedAt}) found from a prior " +
                    "AmbiguousUnresolved outcome; consuming it now. This ticker is fully eligible again " +
                    "on its next candidate - no further gating.",
                    ticker, pendingSkip.Id, pendingSkip.CreatedAt);
                return;
            }

            var orderFiringService = scope.ServiceProvider.GetRequiredService<OrderFiringService>();

            var fireResult = await orderFiringService.FireAsync(candidate.GateScore, candidate.AdjusterAllocation, stoppingToken);

            // Only proceed to fill-confirmation if firing actually placed a real order - every
            // other outcome (pre-fire-gate rejection, zero-quantity skip, quote-fetch failure,
            // broker rejection, or an unresolved ambiguous failure) means no real order exists
            // for FillConfirmationService to poll.
            if (fireResult.Outcome != OrderFiringOutcome.Fired)
            {
                _logger.LogWarning(
                    "[ORCHESTRATION-WORKER]: {Ticker}: OPEN not fired - {Outcome} ({Reason})",
                    ticker, fireResult.Outcome, fireResult.Reason);
                return;
            }

            var fillConfirmationService = scope.ServiceProvider.GetRequiredService<FillConfirmationService>();
            var maxWait = TimeSpan.FromSeconds(settings.FillConfirmationMaxWaitSeconds);

            var confirmResult = await fillConfirmationService.ConfirmOpenAsync(
                fireResult.ClientOrderId!, candidate.GateScore, candidate.AdjusterAllocation, maxWait, stoppingToken);

            _logger.LogInformation(
                "[ORCHESTRATION-WORKER]: {Ticker}: OPEN fill-confirmation outcome {Outcome}", ticker, confirmResult.Outcome);
        }
        catch (Exception ex)
        {
            // This method runs detached from RunCycleAsync/ExecuteAsync - an unhandled
            // exception here would otherwise become an unobserved task exception rather than a
            // loop crash, but it must still be logged, not silently lost. No candidate's
            // exception may crash the polling loop (CLAUDE.md's Execution Layer: Sixth Design
            // Decision).
            _logger.LogError(ex, "[ORCHESTRATION-WORKER]: {Ticker}: unhandled exception processing OPEN candidate", ticker);
        }
        finally
        {
            // Covers every outcome above: dry-run log-only, pre-fire-gate rejection, terminal
            // firing failure, fill-confirmation timeout, and an unhandled exception. No
            // candidate's failure may leave it stuck in-flight forever.
            _inFlightTracker.Release(ticker);
        }
    }

    private async Task ProcessCloseCandidateAsync(CloseAction closeAction, CancellationToken stoppingToken)
    {
        var position = closeAction.Position;
        var ticker = position.Ticker;

        try
        {
            var settings = _settings.Value;

            if (closeAction.FromCompositeDegradation && closeAction.FromPlateauDeactivation)
            {
                // Overlap case: both conditions genuinely apply to this position at once
                // (CLAUDE.md's Execution Layer: Candidate Orchestrator Correction). Logged
                // explicitly rather than silently resolved - composite-degradation still wins
                // for ExitReason below, but the plateau signal is not discarded from the record.
                _logger.LogInformation(
                    "[ORCHESTRATION-WORKER]: {Ticker}: Position {PositionId} flagged by BOTH composite-degradation " +
                    "and plateau-deactivation - composite-degradation takes priority for ExitReason",
                    ticker, position.Id);
            }

            // Composite degradation takes priority when both conditions apply - the more
            // specific, more recently-triggered signal. CandidateOrchestrator guarantees at
            // least one flag is true for every CloseAction it produces, so the else branch is
            // exactly the plateau-only case, never an unflagged fallback.
            var exitReason = closeAction.FromCompositeDegradation
                ? PositionExitReason.CompositeDegradation
                : PositionExitReason.PlateauDeactivation;

            if (settings.IsDryRunEnabled)
            {
                _logger.LogInformation(
                    "[ORCHESTRATION-WORKER]: DRY-RUN: would fire CLOSE for {Ticker} (PositionId={PositionId}, " +
                    "EntryQuantity={EntryQuantity}, EntryPrice={EntryPrice}, ExitReason={ExitReason})",
                    ticker, position.Id, position.EntryQuantity, position.EntryPrice, exitReason);
                return;
            }

            using var scope = _scopeFactory.CreateScope();
            var orderFiringService = scope.ServiceProvider.GetRequiredService<OrderFiringService>();

            var fireResult = await orderFiringService.FireCloseAsync(position, stoppingToken);

            if (fireResult.Outcome != OrderFiringOutcome.Fired)
            {
                _logger.LogWarning(
                    "[ORCHESTRATION-WORKER]: {Ticker}: CLOSE not fired - {Outcome} ({Reason})",
                    ticker, fireResult.Outcome, fireResult.Reason);
                return;
            }

            var fillConfirmationService = scope.ServiceProvider.GetRequiredService<FillConfirmationService>();
            var maxWait = TimeSpan.FromSeconds(settings.FillConfirmationMaxWaitSeconds);

            var confirmResult = await fillConfirmationService.ConfirmCloseAsync(
                fireResult.ClientOrderId!, position, exitReason, maxWait, stoppingToken);

            _logger.LogInformation(
                "[ORCHESTRATION-WORKER]: {Ticker}: CLOSE fill-confirmation outcome {Outcome}", ticker, confirmResult.Outcome);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ORCHESTRATION-WORKER]: {Ticker}: unhandled exception processing CLOSE candidate", ticker);
        }
        finally
        {
            _inFlightTracker.Release(ticker);
        }
    }
}
