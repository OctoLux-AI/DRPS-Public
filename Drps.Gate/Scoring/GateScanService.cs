using Drps.Calculator.Atr;
using Drps.Calculator.Dma;
using Drps.Calculator.Persistence;
using Drps.Calculator.Rsi;
using Drps.Calculator.Rvol;
using Drps.Calculator.Verification;
using Drps.Ingestion.Persistence;
using Drps.Ingestion.Verification;
using Drps.Ledger;
using Drps.Shared.Models;
using Drps.Shared.Positioning;
using Drps.Shared.Scoring;
using Microsoft.EntityFrameworkCore;

namespace Drps.Gate.Scoring;

/// <summary>
/// Orchestrates one full Gate scan: discovers which tickers Drps.Calculator has recently
/// produced a full DMA/RSI/RVOL/ATR indicator set for, resolves and verifies each one, runs
/// it through GateQualityScorer then GateCompositeService, and persists a GateScore row for
/// every non-rejected result. Ties together pieces that already exist but were never wired
/// into an actual scan run before this class.
///
/// Fully DI-resolvable: every constructor dependency is a real, container-registered
/// service, so a future dependency addition here is caught by normal DI resolution rather
/// than going stale in a hand-written call site. The one runtime-only value - the as-of
/// timestamp - is deliberately NOT a constructor dependency; it's a RunScanAsync parameter,
/// supplied at the point of use. This is the intended pattern for any future Drps.Gate/
/// Drps.Adjuster service needing the same clock seam: resolve the service from DI normally,
/// pass the caller's own already-resolved `Func&lt;DateTime&gt;`-produced timestamp into the
/// method call - never bake a clock into construction, and never call DateTime.Now inside
/// the service itself.
/// </summary>
public class GateScanService
{
    // Gate's own scoring-logic version, hardcoded to 1 for now - a separate concept from
    // GateParameterVersion (which is now the real active GateParameters.Id, resolved fresh
    // at the start of every scan below, not a placeholder).
    private const int PlaceholderCalculationVersion = 1;

    // Recency thresholds for candidate discovery reuse each indicator's own already-defined
    // window-size convention from Drps.Calculator's gap-check/verification logic, rather than
    // inventing a new staleness threshold. DMA has no single WindowSize constant (windows are
    // a per-value array); its widest window (60) is used since that's the largest trailing
    // lookback any DMA value depends on, making it the most conservative bound.
    private static readonly int DmaRecencyWindowDays = DmaCalculator.Windows.Max();
    private const int RsiRecencyWindowDays = RsiGapChecker.WindowSize;
    private const int RvolRecencyWindowDays = RvolCalculator.WindowSize;
    private const int AtrRecencyWindowDays = AtrGapChecker.WindowSize;

    private const string PriceResolution = "1Day";

    private enum TickerScanOutcome
    {
        Scored,
        Rejected,
        Skipped,

        // Sleeper Bucket (CLAUDE.md 2026-08-04) - a real GateScore row was written, but via
        // the observational Sleeper path rather than normal composite scoring. Tracked as its
        // own outcome (not folded into Scored) so RunScanAsync's summary log makes this
        // research-tracking activity visible on its own, distinct from real candidate scoring.
        Sleeper
    }

    private readonly CalculatorDbContext _calculatorDbContext;
    private readonly DrpsDbContext _drpsDbContext;
    private readonly DmaVerificationJoinService _dmaVerificationJoinService;
    private readonly RsiVerificationJoinService _rsiVerificationJoinService;
    private readonly RsiSlopeVerificationJoinService _rsiSlopeVerificationJoinService;
    private readonly RvolVerificationJoinService _rvolVerificationJoinService;
    private readonly AtrVerificationJoinService _atrVerificationJoinService;
    private readonly SectorLookupService _sectorLookupService;
    private readonly EarningsLookupService _earningsLookupService;
    private readonly IPositionStateProvider _positionStateProvider;
    private readonly LedgerLifecycleStampService _lifecycleStampService;
    private readonly ILogger<GateScanService> _logger;

    public GateScanService(
        CalculatorDbContext calculatorDbContext,
        DrpsDbContext drpsDbContext,
        DmaVerificationJoinService dmaVerificationJoinService,
        RsiVerificationJoinService rsiVerificationJoinService,
        RsiSlopeVerificationJoinService rsiSlopeVerificationJoinService,
        RvolVerificationJoinService rvolVerificationJoinService,
        AtrVerificationJoinService atrVerificationJoinService,
        SectorLookupService sectorLookupService,
        EarningsLookupService earningsLookupService,
        IPositionStateProvider positionStateProvider,
        LedgerLifecycleStampService lifecycleStampService,
        ILogger<GateScanService> logger)
    {
        _calculatorDbContext = calculatorDbContext;
        _drpsDbContext = drpsDbContext;
        _dmaVerificationJoinService = dmaVerificationJoinService;
        _rsiVerificationJoinService = rsiVerificationJoinService;
        _rsiSlopeVerificationJoinService = rsiSlopeVerificationJoinService;
        _rvolVerificationJoinService = rvolVerificationJoinService;
        _atrVerificationJoinService = atrVerificationJoinService;
        _sectorLookupService = sectorLookupService;
        _earningsLookupService = earningsLookupService;
        _positionStateProvider = positionStateProvider;
        _lifecycleStampService = lifecycleStampService;
        _logger = logger;
    }

    // `asOf` is supplied by the caller, not read from an internally-held clock - see this
    // class's own doc comment for why. Never call DateTime.Now below this line; every date
    // derived from "now" must trace back to this parameter.
    public async Task RunScanAsync(DateTime asOf, CancellationToken cancellationToken)
    {
        var asOfDate = DateOnly.FromDateTime(asOf);

        // Fail-closed: Gate must never score against a guessed/bootstrap set of thresholds.
        // Checked explicitly (zero rows, or more than one - which should never happen given
        // GateParametersSeeder's own duplicate-active-row guard, but is verified here rather
        // than assumed) before any candidate discovery or scoring is attempted.
        var activeParametersRows = await _drpsDbContext.GateParameters
            .Where(p => p.IsActive)
            .ToListAsync(cancellationToken);

        if (activeParametersRows.Count == 0)
        {
            _logger.LogCritical(
                "[GATE-SCAN]: Gate scan aborted: no active GateParameters row found - seed one before Gate can run");
            return;
        }

        if (activeParametersRows.Count > 1)
        {
            _logger.LogCritical(
                "[GATE-SCAN]: Gate scan aborted: {Count} active GateParameters rows found (expected exactly 1) - " +
                "this should never happen, investigate before Gate can run",
                activeParametersRows.Count);
            return;
        }

        var gateParameters = activeParametersRows[0];

        // Closes the latent gap CLAUDE.md flags: the migration's SQL-level column defaults
        // are 0/0m for every threshold column, while GateParametersSeeder always sets real
        // values explicitly - today the seeder is the only insert path, so an all-zero row is
        // latent, not active, but nothing previously checked that an "active" row's values
        // were actually sane, only that exactly one existed. Same fail-closed shape as the
        // missing/multiple-active-row case above, just a different root cause.
        var violations = GateParametersValidator.Validate(gateParameters);
        if (violations.Count > 0)
        {
            _logger.LogCritical(
                "[GATE-SCAN]: Gate scan aborted: active GateParameters row (Id={GateParametersId}) failed validation: {Violations}",
                gateParameters.Id, string.Join("; ", violations));
            return;
        }

        var qualityScorer = new GateQualityScorer(gateParameters);
        var compositeService = new GateCompositeService(gateParameters);

        var candidates = await DiscoverCandidateTickersAsync(asOfDate, cancellationToken);
        _logger.LogInformation(
            "[GATE-SCAN]: {Count} candidate ticker(s) discovered as of {AsOfDate}", candidates.Count, asOfDate);

        var scoredCount = 0;
        var rejectedCount = 0;
        var skippedCount = 0;
        var failedCount = 0;
        var sleeperCount = 0;

        foreach (var ticker in candidates)
        {
            // One ticker's failure must never stop the scan for the rest - same isolation
            // principle as Drps.Ingestion's IngestionRunner (one bad source doesn't block
            // the others).
            try
            {
                var outcome = await ScoreTickerAsync(
                    ticker, asOf, asOfDate, gateParameters, qualityScorer, compositeService, cancellationToken);
                switch (outcome)
                {
                    case TickerScanOutcome.Scored:
                        scoredCount++;
                        break;
                    case TickerScanOutcome.Rejected:
                        rejectedCount++;
                        break;
                    case TickerScanOutcome.Skipped:
                        skippedCount++;
                        break;
                    case TickerScanOutcome.Sleeper:
                        sleeperCount++;
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A real shutdown request, not a scoring failure - must propagate so the host
                // actually stops, not get logged-and-continued like the catch below.
                throw;
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(ex, "[GATE-SCAN]: unexpected exception scoring {Ticker}", ticker);
            }
        }

        _logger.LogInformation(
            "[GATE-SCAN]: scan complete as of {AsOfDate}: {ScoredCount} scored, {RejectedCount} rejected, " +
            "{SkippedCount} skipped (incomplete data), {SleeperCount} sleeper (observational only), {FailedCount} failed",
            asOfDate, scoredCount, rejectedCount, skippedCount, sleeperCount, failedCount);

        // Plateau reassessment (2026-07-18 decision block, point 3) - a separate sweep over
        // every currently-open Position, independent of the candidate-discovery loop above.
        // A held ticker's own indicator data can be stale/incomplete on a given day even while
        // it remains open, so this deliberately does not reuse `candidates` - it queries
        // Positions directly and evaluates whatever data exists for each one.
        await EvaluatePlateauReassessmentsAsync(asOf, asOfDate, gateParameters, cancellationToken);
    }

    // A ticker only becomes a candidate because Calculator actually produced indicators for
    // it - no hardcoded watchlist, keeping Gate in lockstep with the real pipeline with no
    // separate config to drift. "<= asOfDate" on every branch guards against look-ahead bias
    // (CLAUDE.md's Drps.Monolith framing) - a historical replay must never see indicator rows
    // computed "after" the date it's replaying.
    private async Task<List<string>> DiscoverCandidateTickersAsync(DateOnly asOfDate, CancellationToken cancellationToken)
    {
        var dmaThreshold = asOfDate.AddDays(-DmaRecencyWindowDays);
        var rsiThreshold = asOfDate.AddDays(-RsiRecencyWindowDays);
        var rvolThreshold = asOfDate.AddDays(-RvolRecencyWindowDays);
        var atrThreshold = asOfDate.AddDays(-AtrRecencyWindowDays);

        var dmaSymbols = await _calculatorDbContext.DmaIndicators
            .Where(d => d.BarDate >= dmaThreshold && d.BarDate <= asOfDate)
            .Select(d => d.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);

        var rsiSymbols = await _calculatorDbContext.RsiIndicators
            .Where(r => r.BarDate >= rsiThreshold && r.BarDate <= asOfDate)
            .Select(r => r.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);

        var rvolSymbols = await _calculatorDbContext.RvolIndicators
            .Where(r => r.BarDate >= rvolThreshold && r.BarDate <= asOfDate)
            .Select(r => r.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);

        var atrSymbols = await _calculatorDbContext.AtrIndicators
            .Where(a => a.BarDate >= atrThreshold && a.BarDate <= asOfDate)
            .Select(a => a.Symbol)
            .Distinct()
            .ToListAsync(cancellationToken);

        return dmaSymbols
            .Intersect(rsiSymbols)
            .Intersect(rvolSymbols)
            .Intersect(atrSymbols)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<TickerScanOutcome> ScoreTickerAsync(
        string ticker,
        DateTime asOf,
        DateOnly asOfDate,
        GateParameters gateParameters,
        GateQualityScorer qualityScorer,
        GateCompositeService compositeService,
        CancellationToken cancellationToken)
    {
        var dma = await ResolveDmaAsync(ticker, asOfDate, cancellationToken);
        var rsi = await GetLatestRsiAsync(ticker, asOfDate, cancellationToken);
        var rvol = await GetLatestRvolAsync(ticker, asOfDate, cancellationToken);
        var atr = await GetLatestAtrAsync(ticker, asOfDate, cancellationToken);

        // Candidate discovery already confirmed a recent row of each type exists, but that
        // looser per-type check doesn't guarantee DMA has all four windows on one shared
        // date - null-guard defensively rather than assume discovery's check is sufficient.
        if (dma.BarDate is null || rsi is null || rvol is null || atr is null)
        {
            _logger.LogWarning(
                "[GATE-SCAN]: {Ticker}: incomplete indicator data despite passing candidate discovery, skipping", ticker);
            return TickerScanOutcome.Skipped;
        }

        // Staleness detection (this session's scheduling-resilience audit) - DataAsOfDate is
        // the oldest of the four contributing BarDates (a candidate is only as fresh as its
        // stalest input). Compared against asOf via the same weekday-only TradingDayCalendar
        // already used for plateau reassessment below, not a live trading-calendar HTTP call:
        // GateScanService's own doc comment states it makes no external HTTP calls, and a
        // network dependency here would be a real regression against that invariant just to
        // resolve an edge case. The tradeoff, stated plainly: a gap of exactly one weekday is
        // the expected healthy state (yesterday's close, computed hours ago this morning, or a
        // Friday->Monday weekend gap - CountElapsedTradingDays' weekday counting already
        // absorbs the weekend correctly) and is never flagged. A gap of two or more weekdays is
        // flagged - this correctly catches a genuine multi-day Ingestion/Calculator outage, but
        // can also rarely false-positive on a real multi-day holiday cluster (e.g. a holiday
        // adjacent to a weekend), since this weekday-only calendar has no holiday awareness,
        // same accepted simplification TradingDayCalendar's own doc comment and
        // NoBuyExpirationCalculator already carry elsewhere in this codebase. Per this task's
        // explicit scope decision: flag for human review rather than guess further, and never
        // block scoring on this - a flagged candidate is still scored and bucketed normally.
        var dataAsOfDate = new[] { dma.BarDate.Value, rsi.BarDate, rvol.BarDate, atr.BarDate }.Min();
        var tradingDaysStale = TradingDayCalendar.CountElapsedTradingDays(dataAsOfDate.ToDateTime(TimeOnly.MinValue), asOf);
        var isStaleData = tradingDaysStale > 1;

        if (isStaleData)
        {
            _logger.LogWarning(
                "[GATE-SCAN]: {Ticker}: indicator data is stale - DataAsOfDate={DataAsOfDate}, {TradingDaysStale} trading day(s) behind {AsOfDate} (pipeline outage or holiday cluster - flagged on GateScore.IsStaleData, not blocked)",
                ticker, dataAsOfDate, tradingDaysStale, asOfDate);
        }

        // Blast-radius scoping (CLAUDE.md 2026-08-04) - each DMA window's verification is
        // resolved independently at its own bar count, not collapsed into one "all four
        // AND'd" boolean the way IsDmaFullyVerifiedAsync does (that helper is now used only
        // by plateau reassessment below, deliberately kept strict - see its own doc comment).
        // A bad bar 45 trading days old fails IsDma60Verified without touching the other
        // three, since their own rolling lookbacks never reach that far back.
        var isDma5Verified = await _dmaVerificationJoinService.IsDmaVerifiedAsync(ticker, dma.BarDate.Value, 5, cancellationToken);
        var isDma15Verified = await _dmaVerificationJoinService.IsDmaVerifiedAsync(ticker, dma.BarDate.Value, 15, cancellationToken);
        var isDma30Verified = await _dmaVerificationJoinService.IsDmaVerifiedAsync(ticker, dma.BarDate.Value, 30, cancellationToken);
        var isDma60Verified = await _dmaVerificationJoinService.IsDmaVerifiedAsync(ticker, dma.BarDate.Value, 60, cancellationToken);
        var isRsiVerified = await _rsiVerificationJoinService.IsRsiVerifiedAsync(ticker, rsi.BarDate, cancellationToken);
        var isRvolVerified = await _rvolVerificationJoinService.IsRvolVerifiedAsync(ticker, rvol.BarDate, cancellationToken);
        var isAtrVerified = await _atrVerificationJoinService.IsAtrVerifiedAsync(ticker, atr.BarDate, cancellationToken);

        // Sleeper Bucket (CLAUDE.md 2026-08-04) - only ever meaningful when DMA is misaligned;
        // GateQualityScorer never consults this field once DMA is aligned, so resolving it
        // unconditionally would just be a wasted round-trip on the common (DMA-aligned) path.
        var isSleeperEligible = dma.IsAligned
            ? false
            : await ResolveSleeperEligibilityAsync(ticker, rsi.BarDate, cancellationToken);

        // Earnings Blackout Gate Decision (CLAUDE.md 2026-07-19) - resolved once per candidate,
        // independent of the DMA/RSI/RVOL/ATR indicator lookups above (earnings data has no
        // BarDate/indicator anchor of its own to join against).
        var earningsStatus = await _earningsLookupService.GetBlackoutStatusAsync(ticker, asOf, cancellationToken);

        var candidateInput = new GateCandidateInput
        {
            IsDmaAligned = dma.IsAligned,
            IsDma5Verified = isDma5Verified,
            IsDma15Verified = isDma15Verified,
            IsDma30Verified = isDma30Verified,
            IsDma60Verified = isDma60Verified,
            RsiValue = rsi.Value,
            IsRsiVerified = isRsiVerified,
            RvolValue = rvol.Value,
            IsRvolVerified = isRvolVerified,
            IsAtrVerified = isAtrVerified,
            // Gate: Third Design Decision's clean-pre-event ATR lookup isn't built yet - a
            // future task once that ex-div-relative threshold logic actually exists.
            AtrCleanPreEventValue = null,
            ScoredThroughExDividendEvent = dma.HasExDividendEvent
                || rsi.HasExDividendEvent
                || rvol.HasExDividendEvent
                || atr.HasExDividendEvent,
            VerificationScopeLimited = rsi.VerificationScopeLimited || atr.VerificationScopeLimited,
            // Data-quality provenance (see GateScore's own doc comment for the full "why") -
            // true if ANY of the four indicator rows that fed this candidate had their Close
            // resolved via BarReconciliationService's narrow OHL-agreed/Close-resolved-to-
            // Tiingo exception anywhere in their contributing lookback window.
            HasTiingoCorrectedData = dma.HasTiingoCorrectedClose
                || rsi.HasTiingoCorrectedClose
                || rvol.HasTiingoCorrectedClose
                || atr.HasTiingoCorrectedClose,
            IsEarningsBlackoutActive = earningsStatus == EarningsBlackoutStatus.BlackoutActive,
            IsEarningsDataUnverified = earningsStatus == EarningsBlackoutStatus.Unverified,
            IsSleeperEligible = isSleeperEligible
        };

        // No backstop stop-control mechanism exists anywhere in the system yet - always
        // false for now, passed through as a parameter rather than hardcoded inside
        // GateQualityScorer itself (Gate: First Design Decision's ATR trade-safety check).
        const bool hasBackstopStopControl = false;
        var qualityResult = qualityScorer.Score(candidateInput, hasBackstopStopControl);

        if (qualityResult.IsRejected)
        {
            // Sleeper Bucket (CLAUDE.md 2026-08-04) - the one rejection reason that gets a
            // special second look before falling through to the ordinary rejection-row path
            // below. Every other rejection reason (DmaNotVerified, RsiNotVerified,
            // RsiOutsideQualityBand, AtrUnverifiedNoBackstop) is completely untouched by this
            // feature - only DmaNotAligned is ever eligible for a Sleeper/tracking row.
            if (qualityResult.RejectionReason == GateRejectionReason.DmaNotAligned)
            {
                if (qualityResult.IsSleeperEligible)
                {
                    await PersistSleeperTrackingRowAsync(
                        ticker, asOf, dataAsOfDate, isStaleData, gateParameters, rsi, rvol, atr, qualityResult,
                        GateBucket.Sleeper, cancellationToken);
                    _logger.LogInformation(
                        "[GATE-SCAN]: {Ticker}: DMA misaligned but RsiSlope/RsiConcavity confirmed positive and " +
                        "verified - Sleeper row written (observational only, never fireable)", ticker);
                    return TickerScanOutcome.Sleeper;
                }

                // Not currently Sleeper-eligible - but if this ticker has EVER been flagged
                // Sleeper before, silently going back to discarding it would make "momentum
                // genuinely faded" indistinguishable from "a pipeline error stopped
                // considering it," exactly the ambiguity CLAUDE.md's fail-closed-over-
                // silent-gaps philosophy exists to avoid. A ticker that's never been Sleeper
                // keeps today's unchanged silent-discard behavior - there's nothing to
                // explicitly track yet.
                var hasPriorSleeperRow = await _drpsDbContext.GateScores
                    .AnyAsync(s => s.Ticker == ticker && s.Bucket == GateBucket.Sleeper && s.ScanDate < asOf, cancellationToken);

                if (hasPriorSleeperRow)
                {
                    await PersistSleeperTrackingRowAsync(
                        ticker, asOf, dataAsOfDate, isStaleData, gateParameters, rsi, rvol, atr, qualityResult,
                        GateBucket.Neutral, cancellationToken);
                    _logger.LogInformation(
                        "[GATE-SCAN]: {Ticker}: DMA misaligned, previously flagged Sleeper, no longer qualifies - " +
                        "explicit Neutral row written this date (not silently omitted); prior Sleeper row(s) untouched",
                        ticker);
                    return TickerScanOutcome.Sleeper;
                }
            }

            // Gate: Rejection Reasons Now Persisted (CLAUDE.md 2026-08-06) - a rejection is
            // still never a composite-scored decision (no GateCompositeService call, same as
            // before), but it now gets a real GateScore row (Bucket=Neutral, RejectionReason
            // set) instead of being silently discarded - closing the "no persisted trail" gap
            // the SBUX 2026-08-06 scan surfaced (four separate manual queries needed to
            // reconstruct a rejection cause that had left no trace at all).
            await PersistRejectionRowAsync(
                ticker, asOf, dataAsOfDate, isStaleData, gateParameters, rsi, rvol, atr, qualityResult, cancellationToken);
            _logger.LogInformation(
                "[GATE-SCAN]: {Ticker}: rejected - {RejectionReason}", ticker, qualityResult.RejectionReason);
            return TickerScanOutcome.Rejected;
        }

        var isCurrentlyHeld = _positionStateProvider.IsCurrentlyHeld(ticker);
        var noBuyExpiration = _positionStateProvider.GetNoBuyExpiration(ticker);
        var compositeResult = compositeService.Score(qualityResult, isCurrentlyHeld, asOf, noBuyExpiration);

        // Composite-degradation exit (Gate: Seventh Design Decision) - the only automated exit
        // signal that exists. Stamps LowGradeDate/CoolDownStartDate onto the currently-open
        // Position for this ticker at recommendation time, per CLAUDE.md's 2026-07-18 decision
        // block (points 1/2/5) - independent of GateScore persistence below, and never touching
        // ExitDate/ExitReason (Ledger's manual-execution-only rule, unchanged).
        if (compositeResult.Bucket == GateBucket.Exit)
        {
            await _lifecycleStampService.StampCompositeDegradationAsync(ticker, asOf, cancellationToken);
        }

        // Null when no fresh (Finnhub, Verified, within the 7-day TTL) sector observation
        // exists - the honest "cannot verify" signal Adjuster's future sector-cap logic
        // must check for, not a fake placeholder category.
        var sector = await _sectorLookupService.GetSectorAsync(ticker, asOf, cancellationToken);

        var gateScore = new GateScore
        {
            ScanDate = asOf,
            Ticker = ticker,
            Sector = sector,
            IsDmaAligned = qualityResult.IsDmaAligned,
            // "Fully verified" retains its original, pre-2026-08-04 meaning (all four
            // windows AND'd) - purely informational now, since Tier 1 gating no longer
            // requires it. Previously this was always true on any written row (rejection
            // guaranteed it); now it can genuinely be false while the row still exists.
            IsDmaVerifiedAsync_Result = qualityResult.IsDma5VerifiedAsyncResult
                && qualityResult.IsDma15VerifiedAsyncResult
                && qualityResult.IsDma30VerifiedAsyncResult
                && qualityResult.IsDma60VerifiedAsyncResult,
            IsDma5VerifiedAsync_Result = qualityResult.IsDma5VerifiedAsyncResult,
            IsDma15VerifiedAsync_Result = qualityResult.IsDma15VerifiedAsyncResult,
            IsDma30VerifiedAsync_Result = qualityResult.IsDma30VerifiedAsyncResult,
            IsDma60VerifiedAsync_Result = qualityResult.IsDma60VerifiedAsyncResult,
            RsiValue = rsi.Value,
            RsiQuality = qualityResult.RsiQuality!.Value,
            RvolValue = rvol.Value,
            RvolQuality = qualityResult.RvolQuality!.Value,
            AtrValue = atr.Value,
            AtrCleanPreEventValue = qualityResult.AtrCleanPreEventValue,
            CompositeScore = compositeResult.CompositeScore,
            Bucket = compositeResult.Bucket,
            ScoredThroughExDividendEvent = qualityResult.ScoredThroughExDividendEvent,
            VerificationScopeLimited = qualityResult.VerificationScopeLimited,
            HasTiingoCorrectedData = qualityResult.HasTiingoCorrectedData,
            HasUnverifiedPartialCreditData = qualityResult.HasUnverifiedPartialCreditData,
            DataAsOfDate = dataAsOfDate,
            IsStaleData = isStaleData,
            CalculationVersion = PlaceholderCalculationVersion,
            GateParameterVersion = gateParameters.Id,
            NoBuyListExpiration = compositeResult.NoBuyListExpiration,
            IsEarningsBlackoutActive = qualityResult.IsEarningsBlackoutActive,
            EarningsDataUnverified = qualityResult.IsEarningsDataUnverified
        };

        _drpsDbContext.GateScores.Add(gateScore);
        await _drpsDbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "[GATE-SCAN]: {Ticker}: scored, CompositeScore={CompositeScore}, Bucket={Bucket}",
            ticker, compositeResult.CompositeScore, compositeResult.Bucket);

        return TickerScanOutcome.Scored;
    }

    // Sleeper Bucket (CLAUDE.md 2026-08-04) - RsiSlope=ConfirmedPositive AND
    // RsiConcavity=ConfirmedPositive AND IsRsiSlopeVerifiedAsync, exactly the three
    // conditions the decision names, no more. Short-circuits cheapest-check-first (slope's
    // own row, then concavity's, then the two-endpoint verification query) to avoid
    // unnecessary DB round-trips once any one condition already fails - same instinct as
    // IsDmaFullyVerifiedAsync's short-circuit above. Anchored at rsiBarDate (RSI's own latest
    // BarDate, already resolved by the caller) rather than DMA's BarDate, since
    // RsiSlopeIndicator/RsiConcavityIndicator are keyed by the RSI series' own dates, not
    // DMA's - using a different anchor here would silently compare a slope/concavity reading
    // against a different day than the RSI value Gate is actually scoring against.
    private async Task<bool> ResolveSleeperEligibilityAsync(string ticker, DateOnly rsiBarDate, CancellationToken cancellationToken)
    {
        var slopeRow = await _calculatorDbContext.RsiSlopeIndicators
            .Where(r => r.Symbol == ticker && r.BarDate == rsiBarDate)
            .OrderByDescending(r => r.ComputedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (slopeRow is null || slopeRow.ConfirmedDirection != SlopeConfirmationDirection.ConfirmedPositive)
        {
            return false;
        }

        var concavityRow = await _calculatorDbContext.RsiConcavityIndicators
            .Where(r => r.Symbol == ticker && r.BarDate == rsiBarDate)
            .OrderByDescending(r => r.ComputedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (concavityRow is null || concavityRow.ConfirmedDirection != SlopeConfirmationDirection.ConfirmedPositive)
        {
            return false;
        }

        // Verification scope is deliberately RsiSlope-only, per this decision's own text -
        // RsiConcavity's ConfirmedPositive state is checked above but not independently
        // verified; no IsRsiConcavityVerifiedAsync exists (or is asked for) today. Uses the
        // slope row's OWN recorded Lookback (not a hardcoded/assumed constant), so this stays
        // correct even if CalculatorSettings.RsiSlopeLookback is ever reconfigured.
        return await _rsiSlopeVerificationJoinService.IsRsiSlopeVerifiedAsync(
            ticker, rsiBarDate, slopeRow.Lookback, cancellationToken);
    }

    // Shared row-builder for both Sleeper outcomes (genuinely eligible today, or previously-
    // Sleeper-but-no-longer-qualifying) - the two cases differ only in `bucket` and log
    // message, not in any other field. Deliberately bypasses GateCompositeService entirely:
    // RsiQuality/RvolQuality/CompositeScore were never computed for a DmaNotAligned rejection
    // (GateQualityScorer.Score returns before reaching that math), so persisting a real
    // composite score here would be fabricating one Gate's own scoring logic never produced -
    // 0m is an honest "not applicable," not a real quality/composite value. RsiValue/RvolValue/
    // AtrValue ARE real (already resolved by the caller before Score() was ever called), so
    // those are the true raw readings, not placeholders.
    private async Task PersistSleeperTrackingRowAsync(
        string ticker,
        DateTime asOf,
        DateOnly dataAsOfDate,
        bool isStaleData,
        GateParameters gateParameters,
        RsiIndicator rsi,
        RvolIndicator rvol,
        AtrIndicator atr,
        GateQualityResult qualityResult,
        GateBucket bucket,
        CancellationToken cancellationToken)
    {
        var sector = await _sectorLookupService.GetSectorAsync(ticker, asOf, cancellationToken);

        var gateScore = new GateScore
        {
            ScanDate = asOf,
            Ticker = ticker,
            Sector = sector,
            IsDmaAligned = false,
            IsDmaVerifiedAsync_Result = qualityResult.IsDma5VerifiedAsyncResult
                && qualityResult.IsDma15VerifiedAsyncResult
                && qualityResult.IsDma30VerifiedAsyncResult
                && qualityResult.IsDma60VerifiedAsyncResult,
            IsDma5VerifiedAsync_Result = qualityResult.IsDma5VerifiedAsyncResult,
            IsDma15VerifiedAsync_Result = qualityResult.IsDma15VerifiedAsyncResult,
            IsDma30VerifiedAsync_Result = qualityResult.IsDma30VerifiedAsyncResult,
            IsDma60VerifiedAsync_Result = qualityResult.IsDma60VerifiedAsyncResult,
            RsiValue = rsi.Value,
            RsiQuality = 0m,
            RvolValue = rvol.Value,
            RvolQuality = 0m,
            AtrValue = atr.Value,
            AtrCleanPreEventValue = qualityResult.AtrCleanPreEventValue,
            CompositeScore = 0m,
            Bucket = bucket,
            ScoredThroughExDividendEvent = qualityResult.ScoredThroughExDividendEvent,
            VerificationScopeLimited = qualityResult.VerificationScopeLimited,
            HasTiingoCorrectedData = qualityResult.HasTiingoCorrectedData,
            HasUnverifiedPartialCreditData = qualityResult.HasUnverifiedPartialCreditData,
            DataAsOfDate = dataAsOfDate,
            IsStaleData = isStaleData,
            CalculationVersion = PlaceholderCalculationVersion,
            GateParameterVersion = gateParameters.Id,
            NoBuyListExpiration = null,
            IsEarningsBlackoutActive = qualityResult.IsEarningsBlackoutActive,
            EarningsDataUnverified = qualityResult.IsEarningsDataUnverified
        };

        _drpsDbContext.GateScores.Add(gateScore);
        await _drpsDbContext.SaveChangesAsync(cancellationToken);
    }

    // Rejection persistence (CLAUDE.md's "Gate: Rejection Reasons Now Persisted,"
    // 2026-08-06) - the ordinary-rejection counterpart to PersistSleeperTrackingRowAsync
    // above, called for every rejection that isn't handled by the Sleeper-specific branches
    // in ScoreTickerAsync (i.e. every RejectionReason other than a Sleeper-eligible or
    // previously-Sleeper DmaNotAligned case). Same shape as PersistSleeperTrackingRowAsync -
    // bypasses GateCompositeService entirely (it throws on IsRejected), Bucket=Neutral (not
    // a new enum value, per the decision's own explicit constraint), CompositeScore=0m as an
    // honest "not applicable," never a fabricated value Gate's own scoring logic never
    // produced.
    //
    // Unlike PersistSleeperTrackingRowAsync (which only ever fires for a DmaNotAligned
    // rejection and can therefore safely hardcode IsDmaAligned=false), this method is called
    // for every rejection cause, so IsDmaAligned/IsDma5-60VerifiedAsync_Result are read
    // straight from qualityResult rather than hardcoded - only a DmaNotAligned rejection can
    // leave IsDmaAligned false; every other cause (DmaNotVerified, RsiNotVerified,
    // RsiOutsideQualityBand, AtrUnverifiedNoBackstop) only fires after DMA alignment already
    // passed.
    //
    // RsiQuality/RvolQuality use whatever GateQualityScorer actually computed before
    // short-circuiting - real, non-null values for RsiOutsideQualityBand and
    // AtrUnverifiedNoBackstop (both compute rsiQuality/rvolQuality before rejecting, per
    // GateQualityScorer.cs), falling back to 0m only when the rejection fired before either
    // was ever computed (DmaNotAligned/DmaNotVerified/RsiNotVerified). RsiValue/RvolValue/
    // AtrValue are the real raw indicator readings (already resolved by the caller before
    // Score() was ever called) - never placeholders, unlike the quality scores above.
    private async Task PersistRejectionRowAsync(
        string ticker,
        DateTime asOf,
        DateOnly dataAsOfDate,
        bool isStaleData,
        GateParameters gateParameters,
        RsiIndicator rsi,
        RvolIndicator rvol,
        AtrIndicator atr,
        GateQualityResult qualityResult,
        CancellationToken cancellationToken)
    {
        var sector = await _sectorLookupService.GetSectorAsync(ticker, asOf, cancellationToken);

        var gateScore = new GateScore
        {
            ScanDate = asOf,
            Ticker = ticker,
            Sector = sector,
            IsDmaAligned = qualityResult.IsDmaAligned,
            IsDmaVerifiedAsync_Result = qualityResult.IsDma5VerifiedAsyncResult
                && qualityResult.IsDma15VerifiedAsyncResult
                && qualityResult.IsDma30VerifiedAsyncResult
                && qualityResult.IsDma60VerifiedAsyncResult,
            IsDma5VerifiedAsync_Result = qualityResult.IsDma5VerifiedAsyncResult,
            IsDma15VerifiedAsync_Result = qualityResult.IsDma15VerifiedAsyncResult,
            IsDma30VerifiedAsync_Result = qualityResult.IsDma30VerifiedAsyncResult,
            IsDma60VerifiedAsync_Result = qualityResult.IsDma60VerifiedAsyncResult,
            RsiValue = rsi.Value,
            RsiQuality = qualityResult.RsiQuality ?? 0m,
            RvolValue = rvol.Value,
            RvolQuality = qualityResult.RvolQuality ?? 0m,
            AtrValue = atr.Value,
            AtrCleanPreEventValue = qualityResult.AtrCleanPreEventValue,
            CompositeScore = 0m,
            Bucket = GateBucket.Neutral,
            RejectionReason = qualityResult.RejectionReason?.ToString(),
            ScoredThroughExDividendEvent = qualityResult.ScoredThroughExDividendEvent,
            VerificationScopeLimited = qualityResult.VerificationScopeLimited,
            HasTiingoCorrectedData = qualityResult.HasTiingoCorrectedData,
            HasUnverifiedPartialCreditData = qualityResult.HasUnverifiedPartialCreditData,
            DataAsOfDate = dataAsOfDate,
            IsStaleData = isStaleData,
            CalculationVersion = PlaceholderCalculationVersion,
            GateParameterVersion = gateParameters.Id,
            NoBuyListExpiration = null,
            IsEarningsBlackoutActive = qualityResult.IsEarningsBlackoutActive,
            EarningsDataUnverified = qualityResult.IsEarningsDataUnverified
        };

        _drpsDbContext.GateScores.Add(gateScore);
        await _drpsDbContext.SaveChangesAsync(cancellationToken);
    }

    // Every currently-open Position, independent of candidate discovery (a held ticker may not
    // appear in `candidates` on a given day - e.g. a genuine gap in one indicator type - but
    // plateau reassessment still needs to run against whatever data actually exists for it).
    // One position's failure/incomplete data must never stop reassessment of the rest - same
    // per-item isolation principle as the candidate loop in RunScanAsync above.
    private async Task EvaluatePlateauReassessmentsAsync(
        DateTime asOf, DateOnly asOfDate, GateParameters gateParameters, CancellationToken cancellationToken)
    {
        var plateauEvaluator = new GatePlateauEvaluator(gateParameters);

        var openPositions = await _drpsDbContext.Positions
            .Where(p => p.ExitDate == null)
            .ToListAsync(cancellationToken);

        var reassessedCount = 0;
        var reactivatedCount = 0;
        var deactivatedCount = 0;
        var skippedCount = 0;
        var failedCount = 0;

        foreach (var position in openPositions)
        {
            try
            {
                var outcome = await EvaluatePlateauForPositionAsync(position, asOf, asOfDate, plateauEvaluator, cancellationToken);
                switch (outcome)
                {
                    case PlateauReassessmentOutcome.Reactivated:
                        reassessedCount++;
                        reactivatedCount++;
                        break;
                    case PlateauReassessmentOutcome.Deactivated:
                        reassessedCount++;
                        deactivatedCount++;
                        break;
                    case PlateauReassessmentOutcome.Skipped:
                        skippedCount++;
                        break;
                    case PlateauReassessmentOutcome.NotTriggered:
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(ex, "[GATE-SCAN]: unexpected exception during plateau reassessment for {Ticker}", position.Ticker);
            }
        }

        _logger.LogInformation(
            "[GATE-SCAN]: plateau reassessment complete as of {AsOfDate}: {ReassessedCount} reassessed " +
            "({ReactivatedCount} reactivated, {DeactivatedCount} deactivated), {SkippedCount} skipped " +
            "(incomplete data or no baseline), {FailedCount} failed",
            asOfDate, reassessedCount, reactivatedCount, deactivatedCount, skippedCount, failedCount);
    }

    private enum PlateauReassessmentOutcome
    {
        NotTriggered,
        Reactivated,
        Deactivated,
        Skipped
    }

    // Resolves all data a reassessment could possibly need (current price, ATR, DMA, RSI,
    // RVOL) up front and bails out (Skipped) if any of it is missing, rather than stamping
    // PlateauDate and then discovering Inadequate can't actually be evaluated - a stamped
    // PlateauDate with no Reactivated/Deactivated resolution would be a confusing half-state.
    private async Task<PlateauReassessmentOutcome> EvaluatePlateauForPositionAsync(
        Position position, DateTime asOf, DateOnly asOfDate, GatePlateauEvaluator plateauEvaluator, CancellationToken cancellationToken)
    {
        var ticker = position.Ticker;

        if (position.PlateauBaselinePrice is null || position.PlateauBaselineDate is null)
        {
            // A position row created before this schema existed - see Position.cs's own doc
            // comment on these two columns. Not guessed at; left for a human to backfill.
            _logger.LogWarning(
                "[GATE-SCAN]: {Ticker}: open Position {PositionId} has no plateau baseline, skipping reassessment",
                ticker, position.Id);
            return PlateauReassessmentOutcome.Skipped;
        }

        var currentPrice = await GetLatestCloseAsync(ticker, asOfDate, cancellationToken);
        var atr = await GetLatestAtrAsync(ticker, asOfDate, cancellationToken);

        if (currentPrice is null || atr is null)
        {
            _logger.LogWarning(
                "[GATE-SCAN]: {Ticker}: missing current price or ATR data, skipping plateau reassessment", ticker);
            return PlateauReassessmentOutcome.Skipped;
        }

        var tradingDaysElapsed = TradingDayCalendar.CountElapsedTradingDays(position.PlateauBaselineDate.Value, asOf);
        var isTriggered = plateauEvaluator.IsTriggered(
            currentPrice.Value, position.PlateauBaselinePrice.Value, atr.Value, tradingDaysElapsed);

        if (!isTriggered)
        {
            return PlateauReassessmentOutcome.NotTriggered;
        }

        // Trigger fired - resolve the remaining data Inadequate needs. Same "resolve
        // everything before stamping anything" reasoning as above: DMA/RSI/RVOL can each be
        // independently missing even when price/ATR exist.
        var dma = await ResolveDmaAsync(ticker, asOfDate, cancellationToken);
        var rsi = await GetLatestRsiAsync(ticker, asOfDate, cancellationToken);
        var rvol = await GetLatestRvolAsync(ticker, asOfDate, cancellationToken);

        if (dma.BarDate is null || rsi is null || rvol is null)
        {
            _logger.LogWarning(
                "[GATE-SCAN]: {Ticker}: plateau trigger fired but DMA/RSI/RVOL data is incomplete, skipping reassessment",
                ticker);
            return PlateauReassessmentOutcome.Skipped;
        }

        // DMA hard-gate reused exactly as-is (Gate: Fifth Design Decision's binary 5>15>30>60
        // alignment + full-verification check) - not reimplemented, see GatePlateauEvaluator's
        // own doc comment for the full "why" behind each of the three Inadequate conditions.
        var isDmaFullyVerified = await IsDmaFullyVerifiedAsync(ticker, dma.BarDate.Value, cancellationToken);
        var isInadequate = plateauEvaluator.IsInadequate(dma.IsAligned, isDmaFullyVerified, rsi.Value, rvol.Value);

        await _lifecycleStampService.StampPlateauReassessmentAsync(
            ticker, asOf, currentPrice.Value, isInadequate, cancellationToken);

        return isInadequate ? PlateauReassessmentOutcome.Deactivated : PlateauReassessmentOutcome.Reactivated;
    }

    // Alpaca is primary/ground truth - same convention BarReconciliationService and
    // AdjusterScanService.GetLatestCloseAsync already use, reused here rather than building a
    // second price-fetch mechanism. "<= cutoff" guards against look-ahead bias, same
    // DateOnly-to-UTC-midnight conversion used everywhere else RawOhlcvBar.Timestamp is queried.
    private async Task<decimal?> GetLatestCloseAsync(string ticker, DateOnly asOfDate, CancellationToken cancellationToken)
    {
        var cutoff = new DateTimeOffset(asOfDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var bar = await _drpsDbContext.RawOhlcvBars
            .Where(b => b.Symbol == ticker && b.Source == SourceType.Alpaca && b.Resolution == PriceResolution && b.Timestamp <= cutoff)
            .OrderByDescending(b => b.Timestamp)
            .FirstOrDefaultAsync(cancellationToken);

        return bar?.Close;
    }

    // "Fully verified" = every one of the 5/15/30/60 windows independently confirmed verified,
    // not just the widest. Short-circuits on the first false result to avoid unnecessary DB
    // round-trips.
    //
    // As of the blast-radius-scoping decision (CLAUDE.md 2026-08-04), this strict "all four
    // AND'd" check is USED ONLY BY PLATEAU REASSESSMENT below - ScoreTickerAsync's own Tier 1
    // candidate-discovery gate resolves each window independently instead (see its own
    // comment) and no longer calls this method. Deliberately kept strict here: a currently-
    // HELD position's continued eligibility is judged at least as conservatively as a brand
    // new candidate's entry, not more leniently - relaxing plateau reassessment to match the
    // new candidate-discovery leniency was considered and declined, since real capital is
    // already committed to a held position and the blast-radius argument (an old, low-weight
    // bad bar has negligible impact on a wide window's value) doesn't by itself justify
    // loosening a check that decides whether to keep or close an existing position.
    private async Task<bool> IsDmaFullyVerifiedAsync(string symbol, DateOnly barDate, CancellationToken cancellationToken)
    {
        foreach (var window in DmaCalculator.Windows)
        {
            if (!await _dmaVerificationJoinService.IsDmaVerifiedAsync(symbol, barDate, window, cancellationToken))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct DmaResolution(DateOnly? BarDate, bool IsAligned, bool HasExDividendEvent, bool HasTiingoCorrectedClose);

    // DMA alignment (Gate: Fifth Design Decision) is a binary, all-or-nothing check across the
    // full 5>15>30>60 stack, so only a BarDate with every one of DmaCalculator.Windows present
    // can be judged at all - a date with only some windows computed is not resolvable, same as
    // if no DMA data existed.
    private async Task<DmaResolution> ResolveDmaAsync(string symbol, DateOnly asOfDate, CancellationToken cancellationToken)
    {
        var rows = await _calculatorDbContext.DmaIndicators
            .Where(d => d.Symbol == symbol && d.BarDate <= asOfDate)
            .ToListAsync(cancellationToken);

        var requiredWindows = DmaCalculator.Windows;
        var completeDateGroup = rows
            .GroupBy(d => d.BarDate)
            .Where(g => requiredWindows.All(w => g.Any(d => d.Window == w)))
            .OrderByDescending(g => g.Key)
            .FirstOrDefault();

        if (completeDateGroup is null)
        {
            return new DmaResolution(null, false, false, false);
        }

        // Most recent row per window, in case of duplicate CalculationVersions for the same
        // (Symbol, BarDate, Window) - same "latest wins" convention used throughout this class.
        var latestByWindow = completeDateGroup
            .GroupBy(d => d.Window)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.ComputedAt).First());

        var isAligned = latestByWindow[5].Value > latestByWindow[15].Value
            && latestByWindow[15].Value > latestByWindow[30].Value
            && latestByWindow[30].Value > latestByWindow[60].Value;

        var hasExDividendEvent = latestByWindow.Values.Any(d => d.HasExDividendEvent);
        var hasTiingoCorrectedClose = latestByWindow.Values.Any(d => d.HasTiingoCorrectedClose);

        return new DmaResolution(completeDateGroup.Key, isAligned, hasExDividendEvent, hasTiingoCorrectedClose);
    }

    // Latest-row lookups for RSI/RVOL/ATR, kept as separate methods rather than one generic
    // helper - same reasoning RsiComputationService's own doc comment gives for keeping
    // indicator types structurally separate: a shared generic base would be complexity bought
    // for a benefit too small to matter at three indicators, and RsiIndicator/AtrIndicator's
    // VerificationScopeLimited field (which RvolIndicator doesn't have) would need type-specific
    // handling anyway. Each has at most one row per (Symbol, BarDate, CalculationVersion); the
    // same "latest wins" convention used everywhere else in this class applies here too.
    private async Task<RsiIndicator?> GetLatestRsiAsync(string symbol, DateOnly asOfDate, CancellationToken cancellationToken)
    {
        return await _calculatorDbContext.RsiIndicators
            .Where(r => r.Symbol == symbol && r.BarDate <= asOfDate)
            .OrderByDescending(r => r.BarDate)
            .ThenByDescending(r => r.ComputedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<RvolIndicator?> GetLatestRvolAsync(string symbol, DateOnly asOfDate, CancellationToken cancellationToken)
    {
        return await _calculatorDbContext.RvolIndicators
            .Where(r => r.Symbol == symbol && r.BarDate <= asOfDate)
            .OrderByDescending(r => r.BarDate)
            .ThenByDescending(r => r.ComputedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<AtrIndicator?> GetLatestAtrAsync(string symbol, DateOnly asOfDate, CancellationToken cancellationToken)
    {
        return await _calculatorDbContext.AtrIndicators
            .Where(a => a.Symbol == symbol && a.BarDate <= asOfDate)
            .OrderByDescending(a => a.BarDate)
            .ThenByDescending(a => a.ComputedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
