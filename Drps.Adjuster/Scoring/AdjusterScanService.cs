using Drps.Adjuster.OptionsFlow;
using Drps.Adjuster.Sizing;
using Drps.Ingestion.Feeders;
using Drps.Ingestion.Persistence;
using Drps.Ingestion.Verification;
using Drps.Ledger;
using Drps.Shared.Models;
using Drps.Shared.Positioning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Drps.Adjuster.Scoring;

/// <summary>
/// Orchestrates one full Adjuster scan: discovers BUY-bucket GateScore rows that haven't
/// already been sized, resolves the total-capital/portfolio-state context each one needs, runs
/// each through AdjusterSizingService, and persists an AdjusterAllocation row for every funded
/// result. Same shape and reasoning as GateScanService - ties together pieces that already
/// exist but were never wired into an actual scan run before this class.
///
/// Fully DI-resolvable: every constructor dependency is a real, container-registered service.
/// The as-of timestamp is a RunScanAsync parameter, not a constructor dependency - same
/// clock-injection pattern GateScanService's own doc comment establishes.
/// </summary>
public class AdjusterScanService
{
    private const string Resolution = "1Day";

    private enum CandidateScanOutcome
    {
        Sized,
        NotFunded,
        Skipped
    }

    // AllocatedDollarAmount is 0 for NotFunded/Skipped - only Sized ever contributes to the
    // running totals RunScanAsync accumulates below.
    private readonly record struct CandidateSizingOutcome(CandidateScanOutcome Outcome, decimal AllocatedDollarAmount);

    private readonly DrpsDbContext _drpsDbContext;
    private readonly AdjusterSizingService _sizingService;
    private readonly AlpacaAccountFeeder _accountFeeder;
    private readonly IPortfolioStateProvider _portfolioStateProvider;
    private readonly InsiderLookupService _insiderLookupService;
    private readonly CboeOptionsChainClient _optionsChainClient;
    private readonly OptionsFlowMultiplierOptions _optionsFlowMultiplierOptions;
    private readonly LedgerLifecycleStampService _lifecycleStampService;
    private readonly ILogger<AdjusterScanService> _logger;

    public AdjusterScanService(
        DrpsDbContext drpsDbContext,
        AdjusterSizingService sizingService,
        AlpacaAccountFeeder accountFeeder,
        IPortfolioStateProvider portfolioStateProvider,
        InsiderLookupService insiderLookupService,
        CboeOptionsChainClient optionsChainClient,
        IOptions<OptionsFlowMultiplierOptions> optionsFlowMultiplierOptions,
        LedgerLifecycleStampService lifecycleStampService,
        ILogger<AdjusterScanService> logger)
    {
        _drpsDbContext = drpsDbContext;
        _sizingService = sizingService;
        _accountFeeder = accountFeeder;
        _portfolioStateProvider = portfolioStateProvider;
        _insiderLookupService = insiderLookupService;
        _optionsChainClient = optionsChainClient;
        _optionsFlowMultiplierOptions = optionsFlowMultiplierOptions.Value;
        _lifecycleStampService = lifecycleStampService;
        _logger = logger;
    }

    // `asOf` is supplied by the caller, not read from an internally-held clock - see this
    // class's own doc comment for why. Never call DateTime.Now below this line.
    public async Task RunScanAsync(DateTime asOf, CancellationToken cancellationToken)
    {
        // Fail-closed: Adjuster must never size against a guessed/bootstrap set of
        // thresholds - same shape as GateScanService's own active-GateParameters check.
        var activeParametersRows = await _drpsDbContext.AdjusterParameters
            .Where(p => p.IsActive)
            .ToListAsync(cancellationToken);

        if (activeParametersRows.Count == 0)
        {
            _logger.LogCritical(
                "[ADJUSTER-SCAN]: Adjuster scan aborted: no active AdjusterParameters row found - seed one before Adjuster can run");
            return;
        }

        if (activeParametersRows.Count > 1)
        {
            _logger.LogCritical(
                "[ADJUSTER-SCAN]: Adjuster scan aborted: {Count} active AdjusterParameters rows found (expected exactly 1) - " +
                "this should never happen, investigate before Adjuster can run",
                activeParametersRows.Count);
            return;
        }

        var parameters = activeParametersRows[0];

        // Same fail-closed shape as GateScanService's GateParametersValidator check - closes
        // the same latent gap (SQL-level column defaults are 0/0m, only the seeder sets real
        // values, nothing previously confirmed an "active" row's values were actually sane).
        var violations = AdjusterParametersValidator.Validate(parameters);
        if (violations.Count > 0)
        {
            _logger.LogCritical(
                "[ADJUSTER-SCAN]: Adjuster scan aborted: active AdjusterParameters row (Id={AdjusterParametersId}) failed validation: {Violations}",
                parameters.Id, string.Join("; ", violations));
            return;
        }

        // Total capital is a precondition for sizing anything at all - unlike a single
        // candidate's own bad data, a failed account fetch blocks the whole scan rather than
        // being isolated per-candidate.
        var accountResult = await _accountFeeder.FetchAccountAsync(cancellationToken);
        if (!accountResult.Success || accountResult.BuyingPower is null)
        {
            _logger.LogCritical(
                "[ADJUSTER-SCAN]: Adjuster scan aborted: failed to fetch total capital from Alpaca ({ErrorMessage})",
                accountResult.ErrorMessage);
            return;
        }

        var totalCapital = accountResult.BuyingPower.Value;
        var portfolioState = await _portfolioStateProvider.GetCurrentStateAsync(cancellationToken);

        var candidates = await DiscoverUnallocatedBuyCandidatesAsync(asOf, cancellationToken);
        _logger.LogInformation(
            "[ADJUSTER-SCAN]: {Count} unallocated BUY candidate(s) discovered as of {AsOf}", candidates.Count, asOf);

        var sizedCount = 0;
        var notFundedCount = 0;
        var skippedCount = 0;
        var failedCount = 0;

        // Scan-local running totals only - never persisted, never read from or written to
        // Ledger. PortfolioState itself is fetched once, above, and never re-queried mid-scan;
        // these two accumulators are what let a later candidate in THIS scan see capital an
        // earlier candidate in THIS SAME scan already committed. Candidates are processed in
        // GateScore.Id order (DiscoverUnallocatedBuyCandidatesAsync's own ordering) - that
        // ordering now has real behavioral significance, not just determinism: it is a
        // first-come-first-sized tie-breaking rule. A candidate that would have fit against
        // the start-of-scan PortfolioState snapshot alone can still be skipped here if an
        // earlier candidate in this same scan already claimed the remaining headroom.
        var runningTotalAllocatedThisScan = 0m;
        var runningAllocatedBySectorThisScan = new Dictionary<string, decimal>();

        foreach (var gateScore in candidates)
        {
            // One candidate's failure must never stop the scan for the rest - same isolation
            // principle as Drps.Ingestion's IngestionRunner and GateScanService.
            try
            {
                var sectorRunningTotal = gateScore.Sector is not null
                    ? runningAllocatedBySectorThisScan.GetValueOrDefault(gateScore.Sector)
                    : 0m;

                var result = await SizeCandidateAsync(
                    gateScore, asOf, parameters, totalCapital, portfolioState,
                    runningTotalAllocatedThisScan, sectorRunningTotal, cancellationToken);

                switch (result.Outcome)
                {
                    case CandidateScanOutcome.Sized:
                        sizedCount++;
                        runningTotalAllocatedThisScan += result.AllocatedDollarAmount;
                        if (gateScore.Sector is not null)
                        {
                            runningAllocatedBySectorThisScan[gateScore.Sector] =
                                runningAllocatedBySectorThisScan.GetValueOrDefault(gateScore.Sector) + result.AllocatedDollarAmount;
                        }
                        break;
                    case CandidateScanOutcome.NotFunded:
                        notFundedCount++;
                        break;
                    case CandidateScanOutcome.Skipped:
                        skippedCount++;
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A real shutdown request, not a sizing failure - must propagate so the host
                // actually stops, not get logged-and-continued like the catch below.
                throw;
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(ex, "[ADJUSTER-SCAN]: unexpected exception sizing {Ticker}", gateScore.Ticker);
            }
        }

        _logger.LogInformation(
            "[ADJUSTER-SCAN]: scan complete as of {AsOf}: {SizedCount} sized, {NotFundedCount} not funded " +
            "(sector cap and/or scan-local reserve-capital ceiling), {SkippedCount} skipped (no price data), " +
            "{FailedCount} failed",
            asOf, sizedCount, notFundedCount, skippedCount, failedCount);
    }

    // BUY-bucket GateScore rows, guarded against look-ahead bias the same way GateScanService
    // guards indicator discovery ("<= asOf"), that don't already have a corresponding
    // AdjusterAllocation row - the mechanism that actually prevents re-sizing something already
    // sized, regardless of how old the GateScore row is. OrderBy(g => g.Id) is now also the
    // scan's first-come-first-sized tie-break (see RunScanAsync's running-total comment) -
    // not just a determinism nicety anymore.
    private async Task<List<GateScore>> DiscoverUnallocatedBuyCandidatesAsync(DateTime asOf, CancellationToken cancellationToken)
    {
        return await _drpsDbContext.GateScores
            .Where(g => g.Bucket == GateBucket.Buy && g.ScanDate <= asOf)
            .Where(g => !_drpsDbContext.AdjusterAllocations.Any(a => a.GateScoreId == g.Id))
            .OrderBy(g => g.Id)
            .ToListAsync(cancellationToken);
    }

    private async Task<CandidateSizingOutcome> SizeCandidateAsync(
        GateScore gateScore,
        DateTime asOf,
        AdjusterParameters parameters,
        decimal totalCapital,
        PortfolioState portfolioState,
        decimal runningTotalAllocatedThisScan,
        decimal runningSectorAllocatedThisScan,
        CancellationToken cancellationToken)
    {
        var asOfDate = DateOnly.FromDateTime(asOf);
        var price = await GetLatestCloseAsync(gateScore.Ticker, asOfDate, cancellationToken);

        if (price is null)
        {
            _logger.LogWarning(
                "[ADJUSTER-SCAN]: {Ticker}: no usable Close price found as of {AsOfDate}, skipping", gateScore.Ticker, asOfDate);
            return new CandidateSizingOutcome(CandidateScanOutcome.Skipped, 0m);
        }

        // Insider Form 4 Adjuster Multiplier Decision (CLAUDE.md 2026-07-19) - resolved once
        // per candidate, independent of the sizing math itself. Always in [1.0,
        // InsiderLookupService.MultiplierCap] - AdjusterSizingService never re-derives or
        // re-clamps this, it only multiplies it in (see that class's own doc comment).
        var insiderResult = await _insiderLookupService.GetMultiplierAsync(gateScore.Ticker, asOf, cancellationToken);

        // Options-flow (CBOE delayed-quotes put/call volume ratio) multiplier - resolved once
        // per candidate, same shape as insiderResult above: fetch once (CboeOptionsChainClient),
        // compute once (OptionsFlowMultiplierService.ComputeMultiplier), never re-fetched
        // anywhere else in this scan. Unlike insiderResult, this signal is not threaded through
        // AdjusterSizingService.ComputeAllocation/AllocationResult - it never participated in
        // the sizing math the way insider historically did, so it is set directly onto the
        // AdjusterAllocation entity below.
        var optionsFlowMultiplier = await GetOptionsFlowMultiplierAsync(gateScore.Ticker, cancellationToken);

        // Sector-cap displacement (Gate: Ninth Design Decision) is still not built - both
        // weakestHolding* params stay null, per AdjusterSizingService's own documented default
        // (Adjuster: Eleventh Decision) - a sector-capped candidate is always skipped, never
        // actually displaces anything today. That is a separate, pre-existing gap this task
        // does not touch. The running-total pair below is this scan's own bookkeeping (see
        // RunScanAsync's own comment on why processing order now matters), distinct from
        // sector-cap/position-count displacement concerns entirely.
        var allocationResult = _sizingService.ComputeAllocation(
            parameters,
            gateScore.CompositeScore,
            gateScore.Sector,
            portfolioState,
            totalCapital,
            price.Value,
            weakestHoldingComposite: null,
            weakestHoldingDollarAmount: null,
            runningTotalAllocatedThisScan: runningTotalAllocatedThisScan,
            runningSectorAllocatedThisScan: runningSectorAllocatedThisScan,
            insiderMultiplier: insiderResult.Multiplier,
            insiderDataUnverified: insiderResult.IsDataUnverified);

        if (!allocationResult.Funded)
        {
            _logger.LogInformation(
                "[ADJUSTER-SCAN]: {Ticker}: not funded - sector cap and/or this scan's running reserve-capital " +
                "ceiling exceeded, no sufficient displacement available",
                gateScore.Ticker);
            return new CandidateSizingOutcome(CandidateScanOutcome.NotFunded, 0m);
        }

        // Position-count-cap displacement (CLAUDE.md's "Adjuster: Concurrent-Position-Cap
        // Displacement, 10% Relative Composite-Score Margin," 2026-08-01) - a separate,
        // account-wide check, independent of the sector-cap/reserve-capital funding decision
        // just above. Evaluated only for a candidate that already cleared funding - this never
        // changes whether THIS candidate gets sized; it only ever flags a DIFFERENT, weaker
        // held position for later closure so room exists once this candidate's fire attempt
        // reaches PreFireGateService's own ConcurrentPositionCap gate.
        await EvaluateConcurrentPositionCapDisplacementAsync(gateScore, asOf, parameters, portfolioState, cancellationToken);

        var allocation = new AdjusterAllocation
        {
            GateScoreId = gateScore.Id,
            AllocationPercent = allocationResult.AllocationPercent,
            AllocationDollarAmount = allocationResult.AllocationDollarAmount,
            ShareCount = allocationResult.ShareCount,
            ShareCapDeficient = allocationResult.ShareCapDeficient,
            AsOfTimestamp = asOf,
            AdjusterParameterVersion = parameters.Id,
            InsiderMultiplierApplied = allocationResult.InsiderMultiplierApplied,
            InsiderDataUnverified = allocationResult.InsiderDataUnverified,
            OptionsFlowMultiplierApplied = optionsFlowMultiplier
        };

        _drpsDbContext.AdjusterAllocations.Add(allocation);
        await _drpsDbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[ADJUSTER-SCAN]: {Ticker}: sized, AllocationDollarAmount={AllocationDollarAmount}, ShareCount={ShareCount}",
            gateScore.Ticker, allocationResult.AllocationDollarAmount, allocationResult.ShareCount);

        return new CandidateSizingOutcome(CandidateScanOutcome.Sized, allocationResult.AllocationDollarAmount);
    }

    // Position-count-cap displacement (CLAUDE.md's "Adjuster: Concurrent-Position-Cap
    // Displacement, 10% Relative Composite-Score Margin," 2026-08-01). Account-wide, NOT
    // sector-scoped - independent of whatever sector-cap displacement logic exists or doesn't
    // (per SizeCandidateAsync's own comment, sector-cap displacement is still unbuilt). Uses
    // the same static PortfolioState snapshot every other check in this scan already uses
    // (fetched once in RunScanAsync, never re-queried mid-scan) - a displacement stamped here
    // does not change OpenPositionCount/WeakestHeldPosition for the rest of THIS scan, since
    // the actual close (and the resulting drop in open-position count) only happens later,
    // once a future automated or manual close path fires it. A position can therefore be
    // stamped for displacement more than once in the same scan by two different qualifying
    // candidates - accepted as a known, low-stakes simplification: DisplacementDate is
    // idempotent to re-stamp (just overwritten with the same/marginally later timestamp), and
    // no ExitDate/ExitReason is ever written by this stamp, so a duplicate stamp causes no
    // double-close or double-count anywhere.
    //
    // Composite score is the sole displacement currency - explicitly no capital-weight or
    // velocity tiebreaker, same reasoning as the sector-cap precedent (Gate: Ninth Design
    // Decision). Single-attempt only, no cascading: unlike the sector-cap capital check, this
    // is a pure 1-for-1 count swap - displacing exactly one held position always frees exactly
    // one slot, which is always exactly enough to make room for exactly one new candidate, so
    // there is no "does one displacement free enough room" calculation to cascade past.
    private async Task EvaluateConcurrentPositionCapDisplacementAsync(
        GateScore gateScore,
        DateTime asOf,
        AdjusterParameters parameters,
        PortfolioState portfolioState,
        CancellationToken cancellationToken)
    {
        if (portfolioState.OpenPositionCount < parameters.MaxConcurrentPositions)
        {
            return;
        }

        var weakest = portfolioState.WeakestHeldPosition;
        if (weakest is null || weakest.Ticker == gateScore.Ticker)
        {
            // No open position to displace, or the account's own weakest holding IS this
            // candidate's ticker (already held) - displacing a position to make room for
            // itself is nonsensical, so this candidate is left for the normal open-candidate
            // dedup (OpenCandidateQuery) to sort out downstream, same as today.
            return;
        }

        var requiredScore = weakest.CompositeScore * (1m + parameters.ConcurrentPositionDisplacementMarginPercent);
        if (gateScore.CompositeScore < requiredScore)
        {
            return;
        }

        _logger.LogInformation(
            "[ADJUSTER-SCAN]: {Ticker}: account at MaxConcurrentPositions ({MaxConcurrentPositions}) - composite " +
            "score {CompositeScore} clears weakest-held {WeakestTicker}'s {WeakestScore} by the required " +
            "{MarginPercent:P0} margin, displacing it",
            gateScore.Ticker, parameters.MaxConcurrentPositions, gateScore.CompositeScore, weakest.Ticker,
            weakest.CompositeScore, parameters.ConcurrentPositionDisplacementMarginPercent);

        await _lifecycleStampService.StampPositionCountDisplacementAsync(weakest.Ticker, asOf, cancellationToken);
    }

    // Fail-closed to exactly 1.0m (neutral). CboeOptionsChainClient.GetPutCallRatioAsync is
    // documented to never throw (every internal failure path returns a null ratio instead,
    // verified by CboeOptionsChainClientTests), so under today's real client this catch is
    // currently unreachable in practice - same "defensive, not dead" category as
    // PreFireGateService.EvaluateCashFloorAsync's own documented unreachable branch. Kept
    // anyway, deliberately, as explicit insurance per this task's own "if the fetch/compute
    // pipeline throws... anywhere upstream" requirement: a future change to either
    // CboeOptionsChainClient or OptionsFlowMultiplierService.ComputeMultiplier (e.g. a
    // misconfigured NeutralThreshold of exactly 0m dividing by zero) could reintroduce a real
    // throw path here, and this guarantees it still degrades to neutral rather than crashing
    // the candidate or leaving a bare default(decimal) 0m. Deliberately NOT folded into the
    // same try/catch as the rest of SizeCandidateAsync (that outer catch, in RunScanAsync's
    // foreach loop, marks the whole candidate "failed" and persists nothing) - a failure here
    // must degrade to neutral and let the candidate still get sized normally, not abort it.
    private async Task<decimal> GetOptionsFlowMultiplierAsync(string ticker, CancellationToken cancellationToken)
    {
        try
        {
            var putCallRatio = await _optionsChainClient.GetPutCallRatioAsync(ticker, cancellationToken);
            return OptionsFlowMultiplierService.ComputeMultiplier(putCallRatio, _optionsFlowMultiplierOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "[ADJUSTER-SCAN]: {Ticker}: options-flow multiplier fetch/compute failed, defaulting to neutral 1.0x", ticker);
            return 1.0m;
        }
    }

    // Alpaca is primary/ground truth (same convention BarReconciliationService already uses
    // when picking which source's value to treat as authoritative) - reused directly rather
    // than building a new price-fetch mechanism. "<= cutoff" guards against look-ahead bias,
    // same DateOnly-to-UTC-midnight conversion GateScanService/BarReconciliationService both
    // already use for RawOhlcvBar's Timestamp convention.
    private async Task<decimal?> GetLatestCloseAsync(string ticker, DateOnly asOfDate, CancellationToken cancellationToken)
    {
        var cutoff = new DateTimeOffset(asOfDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var bar = await _drpsDbContext.RawOhlcvBars
            .Where(b => b.Symbol == ticker && b.Source == SourceType.Alpaca && b.Resolution == Resolution && b.Timestamp <= cutoff)
            .OrderByDescending(b => b.Timestamp)
            .FirstOrDefaultAsync(cancellationToken);

        return bar?.Close;
    }
}
