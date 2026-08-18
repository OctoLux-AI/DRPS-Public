using Drps.Calculator;
using Drps.Calculator.Atr;
using Drps.Calculator.Calendar;
using Drps.Calculator.Dma;
using Drps.Calculator.Dma.RollingDma;
using Drps.Calculator.Rsi;
using Drps.Calculator.Rvol;
using Drps.Calculator.Verification;
using Drps.Ingestion.Feeders;
using Drps.Ingestion.Orchestration;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Drps.Tests;

public class CalculatorWorkerTests
{
    // Fixed instant just before 03:20 - NextRunTimeCalculator.GetNextRunTime(now, 03:20)
    // resolves this to "today's 03:20," a real but tiny (~50ms) delay each cycle. Fixed rather
    // than derived from the real clock so the test is deterministic and fast regardless of
    // when it runs. Same pattern as ExDividendWorkerTests'/WorkerTests' own fixed-clock
    // fixtures - the specific target minute (03:20, not 03:00) also proves this Worker is
    // actually using its own ScheduledRunTime, not silently defaulting to NextRunTimeCalculator's
    // 03:00 constant: if it were, this fixed clock (already past 03:00) would compute a delay to
    // tomorrow's 03:00 instead, and "starting scheduled run" would never appear within the
    // test's deadline.
    private static readonly Func<DateTime> JustBeforeThreeTwenty = () => new DateTime(2026, 7, 15, 3, 19, 59, 950);

    // Matches JustBeforeThreeTwenty's DateOnly (2026-07-15) - CandidateBarVerificationService
    // reconciles only bars whose Timestamp falls inside [asOfDate - LookbackDays, asOfDate],
    // so a bar dated outside that window would silently produce zero BarVerification rows.
    private static RawOhlcvBar MakeBar(SourceType source, string symbol, decimal close) => new()
    {
        Source = source,
        Symbol = symbol,
        Timestamp = new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
        Resolution = "1Day",
        Open = close - 1m,
        High = close + 1m,
        Low = close - 2m,
        Close = close,
        Volume = 1_000_000,
        AdjustmentType = "raw",
        IngestedAt = DateTimeOffset.UtcNow,
        RequestId = Guid.NewGuid()
    };

    private static RollingDmaState AlignedTerm5State(string ticker, DateOnly lastBarDate) => new()
    {
        Ticker = ticker,
        Term = (int)DmaAlignmentTerm.Term5,
        IsAligned = true,
        CalculationVersion = RollingDmaStateService.CalculationVersion,
        LastBarDate = lastBarDate,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task ExecuteAsync_ComputationServiceThrowsUnhandledException_LogsErrorAndContinuesLoopToNextScheduledRun()
    {
        // None of the four computation services are registered, so resolving each one inside
        // Worker's per-symbol loop throws InvalidOperationException for every symbol, every
        // run - a real "unhandled exception during a scheduled run" that must be logged and
        // not stop the loop, so the next cycle's 03:20 timer still gets scheduled. AAA/BBB
        // reach the compute loop via RollingDmaStates (Term5-aligned), replacing the former
        // Watchlist-driven compute scope (CLAUDE.md's "Universe-Wide Dynamic Discovery -
        // Watchlist File Retired from Production Path", 2026-08-05).
        using var calculatorDbContext = InMemoryCalculatorDbContextFactory.Create();
        calculatorDbContext.RollingDmaStates.AddRange(
            AlignedTerm5State("AAA", new DateOnly(2026, 7, 14)),
            AlignedTerm5State("BBB", new DateOnly(2026, 7, 14)));
        await calculatorDbContext.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(calculatorDbContext);
        services.AddScoped<ITradingCalendarService>(_ => new FakeTradingCalendarService());
        services.AddScoped<RollingDmaStateService>();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<Worker>();
        var worker = new Worker(logger, scopeFactory, JustBeforeThreeTwenty);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (logger.Messages.Count(m => m.Contains("starting scheduled run")) < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        cts.Cancel();

        // Unlike the sibling Ingestion/Gate tests, this cycle does 4 services x 2 symbols = 8
        // exceptions per run (vs. 2), which measurably slows each iteration - cancellation can
        // therefore legitimately land while the loop is between iterations (observed only via
        // the `while (!stoppingToken.IsCancellationRequested)` check) rather than inside an
        // active `Task.Delay`. Both a clean return and an OperationCanceledException are
        // correct termination outcomes here; only an unrelated exception would be a real bug.
        try
        {
            await worker.ExecuteTask!;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.Contains(logger.Messages, m => m.Contains("starting scheduled run 1"));
        Assert.Contains(logger.Messages, m => m.Contains("starting scheduled run 2"));
        // 4 services (DMA/RSI/RVOL/ATR) x 2 symbols x >= 2 runs.
        Assert.True(logger.ErrorCount >= 16, $"Expected at least 16 errors (4 services x 2 symbols x 2 runs), got {logger.ErrorCount}");
        Assert.Contains(logger.Messages, m => m.Contains("DMA computation failed for AAA"));
        Assert.Contains(logger.Messages, m => m.Contains("RSI computation failed for AAA"));
        Assert.Contains(logger.Messages, m => m.Contains("RVOL computation failed for AAA"));
        Assert.Contains(logger.Messages, m => m.Contains("ATR computation failed for AAA"));
        Assert.Contains(logger.Messages, m => m.Contains("DMA computation failed for BBB"));
    }

    [Fact]
    public async Task ExecuteAsync_StoppingTokenCancelledDuringSymbolProcessing_PropagatesAndStopsLoop()
    {
        // Simulates a real shutdown arriving mid-run: resolving DmaComputationService is where
        // the cancellation is observed here. The boundary under test is Worker's own catch
        // clause - it must not treat this as a computation failure regardless of where the
        // exception originates, same requirement as Drps.Ingestion's Worker/ExDividendWorker.
        // AAA/BBB reach the compute loop via RollingDmaStates (Term5-aligned).
        using var calculatorDbContext = InMemoryCalculatorDbContextFactory.Create();
        calculatorDbContext.RollingDmaStates.AddRange(
            AlignedTerm5State("AAA", new DateOnly(2026, 7, 14)),
            AlignedTerm5State("BBB", new DateOnly(2026, 7, 14)));
        await calculatorDbContext.SaveChangesAsync();

        var services = new ServiceCollection();
        using var cts = new CancellationTokenSource();
        services.AddLogging();
        services.AddSingleton(calculatorDbContext);
        services.AddScoped<ITradingCalendarService>(_ => new FakeTradingCalendarService());
        services.AddScoped<RollingDmaStateService>();
        services.AddScoped<DmaComputationService>(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException("simulated shutdown mid-run", cts.Token);
        });
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<Worker>();
        var worker = new Worker(logger, scopeFactory, JustBeforeThreeTwenty);

        await worker.StartAsync(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        // Cancellation must propagate, not get logged-and-continued as if it were a
        // computation failure - and the second symbol (BBB, alphabetically after AAA) must
        // never be attempted since the loop stops immediately once the cancellation surfaces.
        Assert.DoesNotContain(logger.Messages, m => m.Contains("computation failed"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("BBB"));
    }

    [Fact]
    public async Task ExecuteAsync_NoNowProviderSupplied_DefaultsToRealClockAndSchedulesAFutureRun()
    {
        // Confirms the production default (no nowProvider override) computes a real,
        // non-negative delay against the actual wall clock, rather than only working when a
        // test seam is supplied.
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var logger = new CapturingLogger<Worker>();
        var worker = new Worker(logger, scopeFactory);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (logger.Messages.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        Assert.Contains(logger.Messages, m => m.Contains("next scheduled run at"));
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyRanSuccessfullyToday_SkipsScheduledRunWithoutAttemptingWork()
    {
        // Idempotency guard (this session's scheduling-resilience audit, Fix 3) - same shape
        // as Drps.Ingestion's equivalent test. RollingDmaStateService is deliberately left
        // unregistered (same "resolution failure simulates a real failure" convention as the
        // test above) - if the guard incorrectly let this run proceed, that would surface as
        // a discovery-failure warning log at minimum; its absence here is the actual proof
        // the guard worked before discovery/computation was ever reached.
        using var dbContext = InMemoryDbContextFactory.Create();
        var today = new DateOnly(2026, 7, 15);
        await WorkerRunGuard.RecordSuccessfulRunAsync(
            dbContext, "Drps.Calculator", today, new DateTime(2026, 7, 15, 3, 20, 0), CancellationToken.None);

        var services = new ServiceCollection();
        services.AddSingleton(dbContext);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<Worker>();
        var worker = new Worker(logger, scopeFactory, JustBeforeThreeTwenty);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!logger.Messages.Any(m => m.Contains("already ran successfully")) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        Assert.Contains(logger.Messages, m => m.Contains("already ran successfully for") && m.Contains("skipping scheduled run"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("DMA computation failed"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("DMA-5-aligned"));
    }

    [Fact]
    public async Task ExecuteAsync_RollingDmaStateMachineThrows_RecordsAllSourcesSucceededFalseWithFailureDetail()
    {
        // Regression test for the swallowed-exception/false-success bug: before this fix,
        // RunCalculatorCycleAsync always passed allSourcesSucceeded: true (the parameter's own
        // default) to WorkerRunGuard.RecordSuccessfulRunAsync regardless of whether the rolling
        // DMA state machine's own try/catch above it had just caught and logged a real failure -
        // WorkerRunRecords showed AllSourcesSucceeded = true on every run since 2026-07-22 even
        // though RollingDmaStateService was throwing "Invalid object name 'RollingDmaStates'" on
        // every single one of them. RollingDmaStateService is deliberately left unregistered here
        // so resolving it inside the rolling-DMA try/catch throws - same "resolution failure
        // simulates a real failure" convention as the unregistered-computation-services test
        // above. Resolving RollingDmaStateService a second time for candidate discovery fails
        // identically, so the candidate list is empty and the per-symbol loop contributes
        // nothing, isolating this assertion to the rolling DMA call site's own success/failure
        // tracking.
        using var dbContext = InMemoryDbContextFactory.Create();
        var services = new ServiceCollection();
        services.AddSingleton(dbContext);
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<Worker>();
        var hostLifetime = new FakeHostApplicationLifetime();

        var worker = new Worker(
            logger, scopeFactory, JustBeforeThreeTwenty,
            hostLifetime, new[] { "--run-now=Drps.Calculator" });

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        Assert.Contains(logger.Messages, m => m.Contains("rolling DMA state machine run failed"));
        Assert.Contains(logger.Messages, m => m.Contains("failed to read DMA-5-aligned candidate tickers"));
        Assert.Contains(logger.Messages, m => m.Contains("no DMA-5-aligned candidate ticker(s) found"));

        var record = Assert.Single(dbContext.WorkerRunRecords);
        Assert.False(record.AllSourcesSucceeded);
        Assert.Contains("RollingDmaStateMachine", record.FailureDetail!);
    }

    [Fact]
    public async Task ExecuteAsync_IndicatorComputeFailsForEverySymbol_RecordsAllSourcesSucceededFalseWithFailureDetailAndLogsSummary()
    {
        // Regression test for the 2026-08-01 audit's false-success gap: before this fix,
        // AllSourcesSucceeded was wired ONLY to the rolling DMA state machine's own outcome
        // (see the sibling test above) - it had no connection to whether any of the per-symbol
        // DMA/RSI/RVOL/ATR writes actually succeeded. On 2026-08-01, a stale published binary
        // caused all 160 of those writes to fail (a NOT NULL column the deployed model didn't
        // know about) while the run still recorded AllSourcesSucceeded = true, because
        // rollingDmaStateMachineSucceeded alone was true. RollingDmaStateService is registered
        // and succeeds trivially here (no UniverseSnapshot rows -> "nothing to process", not a
        // failure - see RollingDmaStateService.RunAsync) so this test isolates the per-symbol
        // compute loop's own contribution to the combined flag. AAA/BBB reach the compute scope
        // via directly-seeded RollingDmaStates (Term5-aligned), replacing the former
        // Watchlist-driven compute scope. All four computation services are deliberately left
        // unregistered (same "resolution failure simulates a real failure" convention as this
        // file's other tests), so every symbol fails all 4 indicators - 2 symbols x 4 indicators
        // (DMA/RSI/RVOL/ATR) = 8 failed writes, 0 succeeded, matching the 40-symbols x 4 = 160
        // -failure shape of the real 8/1 incident at a test-sized scale.
        // Two separate DbContext types, matching production exactly: RollingDmaStateService
        // depends on CalculatorDbContext, while WorkerRunGuard (and the four - deliberately
        // unregistered - computation services) depend on DrpsDbContext. Registering both as
        // singletons lets each service resolve the one it actually needs.
        using var calculatorDbContext = InMemoryCalculatorDbContextFactory.Create();
        calculatorDbContext.RollingDmaStates.AddRange(
            AlignedTerm5State("AAA", new DateOnly(2026, 7, 14)),
            AlignedTerm5State("BBB", new DateOnly(2026, 7, 14)));
        await calculatorDbContext.SaveChangesAsync();

        using var drpsDbContext = InMemoryDbContextFactory.Create();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(calculatorDbContext);
        services.AddSingleton(drpsDbContext);
        services.AddScoped<ITradingCalendarService>(_ => new FakeTradingCalendarService());
        services.AddScoped<RollingDmaStateService>();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<Worker>();
        var hostLifetime = new FakeHostApplicationLifetime();

        var worker = new Worker(
            logger, scopeFactory, JustBeforeThreeTwenty,
            hostLifetime, new[] { "--run-now=Drps.Calculator" });

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        Assert.Contains(logger.Messages, m => m.Contains("compute pass complete for") && m.Contains("2 symbol(s)")
            && m.Contains("8 indicator write(s) attempted") && m.Contains("0 succeeded") && m.Contains("8 failed"));

        var record = Assert.Single(drpsDbContext.WorkerRunRecords);
        Assert.False(record.AllSourcesSucceeded);
        Assert.Contains(
            "DMA/RSI/RVOL/ATR compute: 8 of 8 indicator write(s) failed across 2 symbol(s)",
            record.FailureDetail!);
    }

    [Fact]
    public async Task ExecuteAsync_Dma5AlignedCandidate_IsVerifiedThenReachesComputeLoop()
    {
        // Normal-night flow (CLAUDE.md's "Universe-Wide Dynamic Discovery - Watchlist File
        // Retired from Production Path", 2026-08-05): a DMA-5-aligned candidate
        // (RollingDmaStates) is discovered via GetDma5AlignedTickersAsync, dual-source
        // verified via CandidateBarVerificationService (a real BarVerification row is written
        // using fake Alpaca/Tiingo feeders that agree within tolerance), and only then reaches
        // the per-symbol compute loop. DMA/RSI/RVOL/ATR computation services are deliberately
        // left unregistered (same "resolution failure simulates a real failure" convention as
        // this file's other tests) - a "DMA computation failed for CAND" error log is the proof
        // the candidate reached DmaComputationService.ComputeAsync, not just that it was
        // discovered.
        const string ticker = "CAND";

        using var calculatorDbContext = InMemoryCalculatorDbContextFactory.Create();
        calculatorDbContext.RollingDmaStates.Add(AlignedTerm5State(ticker, new DateOnly(2026, 7, 15)));
        await calculatorDbContext.SaveChangesAsync();

        using var drpsDbContext = InMemoryDbContextFactory.Create();

        var alpacaFeeder = new FakeFeeder(SourceType.Alpaca, (symbol, start, end) => new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Alpaca, symbol, 100.00m) }
        });
        var tiingoFeeder = new FakeFeeder(SourceType.Tiingo, (symbol, start, end) => new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Tiingo, symbol, 100.02m) } // well within tolerance
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(calculatorDbContext);
        services.AddSingleton(drpsDbContext);
        services.AddScoped<ITradingCalendarService>(_ => new FakeTradingCalendarService());
        services.AddScoped<RollingDmaStateService>();
        services.AddSingleton<IMarketDataFeeder>(alpacaFeeder);
        services.AddSingleton<IMarketDataFeeder>(tiingoFeeder);
        services.AddScoped<SourceStatusTracker>();
        services.AddScoped<IngestionRunner>();
        services.AddScoped<BarReconciliationService>();
        services.AddScoped<IngestionJob>();
        services.AddScoped<CandidateBarVerificationService>();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<Worker>();
        var hostLifetime = new FakeHostApplicationLifetime();

        var worker = new Worker(
            logger, scopeFactory, JustBeforeThreeTwenty,
            hostLifetime, new[] { "--run-now=Drps.Calculator" });

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        Assert.Contains(logger.Messages, m => m.Contains("compute scope for")
            && m.Contains("1 DMA-5-aligned candidate ticker(s)"));
        Assert.True(await drpsDbContext.BarVerifications.AnyAsync(v => v.Symbol == ticker),
            "expected CandidateBarVerificationService to have written a BarVerification row for the candidate");
        Assert.Contains(logger.Messages, m => m.Contains($"DMA computation failed for {ticker}"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("candidate bar verification failed"));
    }

    [Fact]
    public async Task ExecuteAsync_NoDma5AlignedCandidatesTonight_LogsClearlyAndCompletesCleanlyWithoutComputing()
    {
        // Empty-candidate-list clean-completion path (CLAUDE.md's "Universe-Wide Dynamic
        // Discovery - Watchlist File Retired from Production Path", 2026-08-05) - a genuinely
        // empty RollingDmaStates table (no ticker currently DMA-5-aligned) is a legitimate,
        // expected nightly outcome ("per today's real data, this happens" per the task's own
        // framing), not an error. RollingDmaStateService is registered and succeeds trivially
        // (no UniverseSnapshot rows -> "nothing to process", not a failure). The run must log
        // this clearly and complete without throwing or silently doing nothing - proven here
        // via a recorded, successful WorkerRunRecord and the total absence of any
        // verification/compute-loop activity.
        using var calculatorDbContext = InMemoryCalculatorDbContextFactory.Create();
        using var drpsDbContext = InMemoryDbContextFactory.Create();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(calculatorDbContext);
        services.AddSingleton(drpsDbContext);
        services.AddScoped<ITradingCalendarService>(_ => new FakeTradingCalendarService());
        services.AddScoped<RollingDmaStateService>();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var logger = new CapturingLogger<Worker>();
        var hostLifetime = new FakeHostApplicationLifetime();

        var worker = new Worker(
            logger, scopeFactory, JustBeforeThreeTwenty,
            hostLifetime, new[] { "--run-now=Drps.Calculator" });

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // No exception must escape a legitimately-empty candidate night - if this hangs or
        // throws, the "complete cleanly" requirement is violated.
        await worker.ExecuteTask!;

        Assert.Contains(logger.Messages, m => m.Contains("no DMA-5-aligned candidate ticker(s) found for")
            && m.Contains("nothing to verify or compute this run"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("computation failed"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("candidate bar verification failed"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("compute scope for"));

        var record = Assert.Single(drpsDbContext.WorkerRunRecords);
        Assert.True(record.AllSourcesSucceeded);
        Assert.Null(record.FailureDetail);
    }

    [Fact]
    public async Task ExecuteAsync_DiscoveredAlignedTicker_ProducesDmaRsiRvolAtrRowsAllTaggedDiscoveredAligned()
    {
        // CLAUDE.md's "Universe-Wide Dynamic Discovery - Watchlist File Retired from
        // Production Path" (2026-08-05) - end-to-end confirmation that a DMA-5-aligned
        // candidate gets real DMA + RSI + RVOL + ATR rows, all tagged
        // TickerSourceOrigin.DiscoveredAligned - not just a DmaIndicator row. Uses real,
        // fully-registered computation services throughout, unlike this file's other tests,
        // which deliberately leave services unregistered to prove inclusion via error logs -
        // this test needs to inspect the actual persisted rows' TickerSourceOrigin.
        using var dbContext = InMemoryCalculatorDbContextFactory.Create();

        const string ticker = "DISCOVERED";
        var firstDay = new DateOnly(2026, 1, 1);
        for (var i = 0; i < 25; i++)
        {
            var timestamp = new DateTimeOffset(firstDay.AddDays(i).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            dbContext.RawOhlcvBars.Add(new RawOhlcvBar
            {
                Source = SourceType.Alpaca,
                Symbol = ticker,
                Timestamp = timestamp,
                Resolution = "1Day",
                Open = 99m + i,
                High = 101m + i,
                Low = 98m + i,
                Close = 100m + i,
                Volume = 1_000_000,
                AdjustmentType = "raw",
                IngestedAt = DateTimeOffset.UtcNow,
                RequestId = Guid.NewGuid()
            });
        }
        dbContext.RollingDmaStates.Add(AlignedTerm5State(ticker, firstDay.AddDays(24)));
        await dbContext.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(dbContext);
        services.AddScoped<ITradingCalendarService>(_ => new FakeTradingCalendarService());
        services.AddScoped<RollingDmaStateService>();
        services.AddScoped<DmaComputationService>();
        services.AddScoped<RsiComputationService>();
        services.AddScoped<RvolComputationService>();
        services.AddScoped<AtrComputationService>();
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        // CandidateBarVerificationService is deliberately left unregistered here - it fails to
        // resolve, gets isolated/logged same as every other optional step, and does not block
        // the compute loop this test is actually inspecting.
        var logger = new CapturingLogger<Worker>();
        var worker = new Worker(logger, scopeFactory, JustBeforeThreeTwenty);

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!logger.Messages.Any(m => m.Contains("scheduled run 1 complete")) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        try
        {
            await worker.ExecuteTask!;
        }
        catch (OperationCanceledException)
        {
        }

        var dmaRows = await dbContext.DmaIndicators.Where(d => d.Symbol == ticker).ToListAsync();
        var rsiRows = await dbContext.RsiIndicators.Where(r => r.Symbol == ticker).ToListAsync();
        var rvolRows = await dbContext.RvolIndicators.Where(r => r.Symbol == ticker).ToListAsync();
        var atrRows = await dbContext.AtrIndicators.Where(a => a.Symbol == ticker).ToListAsync();

        Assert.NotEmpty(dmaRows);
        Assert.NotEmpty(rsiRows);
        Assert.NotEmpty(rvolRows);
        Assert.NotEmpty(atrRows);
        Assert.All(dmaRows, r => Assert.Equal(TickerSourceOrigin.DiscoveredAligned, r.TickerSourceOrigin));
        Assert.All(rsiRows, r => Assert.Equal(TickerSourceOrigin.DiscoveredAligned, r.TickerSourceOrigin));
        Assert.All(rvolRows, r => Assert.Equal(TickerSourceOrigin.DiscoveredAligned, r.TickerSourceOrigin));
        Assert.All(atrRows, r => Assert.Equal(TickerSourceOrigin.DiscoveredAligned, r.TickerSourceOrigin));
    }

    [Fact]
    public async Task ExecuteAsync_NamedRunNowMatchesWorkerName_RunsOnceThenStopsHostWithoutEnteringScheduledLoop()
    {
        // CLAUDE.md's "On-Demand Worker Targeting: --run-now Worker-Name Filter + Clean Exit"
        // (2026-07-27). RollingDmaStateService is deliberately unregistered so the manual run
        // logs errors - what's under test is that the manual run fires immediately,
        // StopApplication is called exactly once, and the scheduled while loop is never entered.
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var logger = new CapturingLogger<Worker>();
        var hostLifetime = new FakeHostApplicationLifetime();

        var worker = new Worker(
            logger, scopeFactory, JustBeforeThreeTwenty,
            hostLifetime, new[] { "--run-now=Drps.Calculator" });

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);
        await worker.ExecuteTask!;

        Assert.Contains(logger.Messages, m => m.Contains("starting manual/on-demand run"));
        Assert.Contains(logger.Messages, m => m.Contains("named on-demand run for Drps.Calculator complete - stopping host"));
        Assert.Equal(1, hostLifetime.StopApplicationCallCount);
        Assert.DoesNotContain(logger.Messages, m => m.Contains("next scheduled run at"));
    }

    [Fact]
    public async Task ExecuteAsync_NamedRunNowTargetsDifferentWorker_DoesNotTriggerManualRunAndEntersScheduledLoop()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var logger = new CapturingLogger<Worker>();
        var hostLifetime = new FakeHostApplicationLifetime();

        var worker = new Worker(
            logger, scopeFactory, JustBeforeThreeTwenty,
            hostLifetime, new[] { "--run-now=Drps.Ingestion" });

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (logger.Messages.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.ExecuteTask!);

        Assert.Contains(logger.Messages, m => m.Contains("next scheduled run at"));
        Assert.DoesNotContain(logger.Messages, m => m.Contains("starting manual/on-demand run"));
        Assert.Equal(0, hostLifetime.StopApplicationCallCount);
    }

    private sealed class FakeFeeder : IMarketDataFeeder
    {
        private readonly Func<string, DateOnly, DateOnly, FeedFetchResult> _resultFactory;

        public FakeFeeder(SourceType source, Func<string, DateOnly, DateOnly, FeedFetchResult> resultFactory)
        {
            Source = source;
            _resultFactory = resultFactory;
        }

        public SourceType Source { get; }

        public Task<FeedFetchResult> FetchDailyBarsAsync(string symbol, DateOnly start, DateOnly end, CancellationToken cancellationToken)
            => Task.FromResult(_resultFactory(symbol, start, end));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = new();
        private readonly object _lock = new();

        public IReadOnlyList<string> Messages
        {
            get { lock (_lock) { return _entries.Select(e => e.Message).ToList(); } }
        }

        public int ErrorCount
        {
            get { lock (_lock) { return _entries.Count(e => e.Level == LogLevel.Error); } }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_lock)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
