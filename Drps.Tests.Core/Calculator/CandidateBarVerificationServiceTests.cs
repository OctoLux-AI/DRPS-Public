using Drps.Calculator.Verification;
using Drps.Ingestion.Feeders;
using Drps.Ingestion.Orchestration;
using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Calculator;

public class CandidateBarVerificationServiceTests
{
    private static readonly DateOnly AsOfDate = new(2026, 8, 5);
    private static readonly DateTimeOffset AsOfTimestamp = new(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);

    private static RawOhlcvBar MakeBar(SourceType source, string symbol, decimal close, DateTimeOffset? timestamp = null) => new()
    {
        Source = source,
        Symbol = symbol,
        Timestamp = timestamp ?? AsOfTimestamp,
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

    private static CandidateBarVerificationService BuildService(
        DrpsDbContext dbContext, IEnumerable<IMarketDataFeeder> feeders)
    {
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);
        var reconciliation = new BarReconciliationService(dbContext, NullLogger<BarReconciliationService>.Instance);

        return new CandidateBarVerificationService(
            feeders,
            dbContext,
            tracker,
            NullLogger<IngestionRunner>.Instance,
            reconciliation,
            NullLogger<CandidateBarVerificationService>.Instance);
    }

    [Fact]
    public async Task VerifyCandidatesAsync_AgreeingSources_WritesVerifiedBarVerificationForEachCandidateTicker()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

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

        var service = BuildService(dbContext, new IMarketDataFeeder[] { alpacaFeeder, tiingoFeeder });

        await service.VerifyCandidatesAsync(new[] { "AAPL", "MSFT" }, AsOfDate, CancellationToken.None);

        var verifications = await dbContext.BarVerifications.ToListAsync();
        Assert.Equal(2, verifications.Count);
        Assert.Contains(verifications, v => v.Symbol == "AAPL");
        Assert.Contains(verifications, v => v.Symbol == "MSFT");
        Assert.All(verifications, v => Assert.True(v.Verified));
    }

    [Fact]
    public async Task VerifyCandidatesAsync_DisagreeingSources_StillWritesUnverifiedBarVerificationAndDiscrepancyNotSwallowed()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var alpacaFeeder = new FakeFeeder(SourceType.Alpaca, (symbol, start, end) => new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Alpaca, symbol, 211.90m) }
        });
        var tiingoFeeder = new FakeFeeder(SourceType.Tiingo, (symbol, start, end) => new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Tiingo, symbol, 220.00m) } // ~3.82% variance, beyond tolerance
        });

        var service = BuildService(dbContext, new IMarketDataFeeder[] { alpacaFeeder, tiingoFeeder });

        await service.VerifyCandidatesAsync(new[] { "GME" }, AsOfDate, CancellationToken.None);

        // A contrary/failed verification is still written, never suppressed or treated as an
        // error to catch and hide.
        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.Equal("GME", verification.Symbol);
        Assert.False(verification.Verified);

        var discrepancy = await dbContext.Discrepancies.SingleAsync();
        Assert.Equal("GME", discrepancy.Symbol);
    }

    [Fact]
    public async Task VerifyCandidatesAsync_EmptyCandidateList_IsNoOpAndDoesNotCallAnyFeederOrThrow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var alpacaFeeder = new FakeFeeder(SourceType.Alpaca, (symbol, start, end) =>
            throw new InvalidOperationException("should never be called for an empty candidate list"));
        var tiingoFeeder = new FakeFeeder(SourceType.Tiingo, (symbol, start, end) =>
            throw new InvalidOperationException("should never be called for an empty candidate list"));

        var service = BuildService(dbContext, new IMarketDataFeeder[] { alpacaFeeder, tiingoFeeder });

        var exception = await Record.ExceptionAsync(
            () => service.VerifyCandidatesAsync(Array.Empty<string>(), AsOfDate, CancellationToken.None));

        Assert.Null(exception);
        Assert.False(await dbContext.RawOhlcvBars.AnyAsync());
        Assert.False(await dbContext.BarVerifications.AnyAsync());
        Assert.False(await dbContext.Discrepancies.AnyAsync());
    }

    [Fact]
    public async Task VerifyCandidatesAsync_ExistingRecentAlpacaCoverage_SkipsLiveAlpacaFetchButStillCallsTiingo()
    {
        // 2026-08-06 audit fix: UniverseBarSweepRunner already wrote a recent Alpaca bar for
        // this ticker (simulated here by seeding RawOhlcvBars directly) before Calculator's own
        // run - the live Alpaca fetch must be skipped entirely and the existing row used
        // instead. The Alpaca feeder throws if invoked, proving it was never called. Tiingo is
        // never skipped - nothing else in the pipeline fetches it for this ticker.
        using var dbContext = InMemoryDbContextFactory.Create();

        dbContext.RawOhlcvBars.Add(MakeBar(SourceType.Alpaca, "AAPL", 100.00m, AsOfTimestamp.AddDays(-1)));
        await dbContext.SaveChangesAsync();

        var alpacaFeeder = new FakeFeeder(SourceType.Alpaca, (symbol, start, end) =>
            throw new InvalidOperationException(
                "live Alpaca fetch should have been skipped - existing coverage is sufficient"));
        var tiingoFeeder = new FakeFeeder(SourceType.Tiingo, (symbol, start, end) => new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Tiingo, symbol, 100.02m, AsOfTimestamp.AddDays(-1)) }
        });

        var service = BuildService(dbContext, new IMarketDataFeeder[] { alpacaFeeder, tiingoFeeder });

        await service.VerifyCandidatesAsync(new[] { "AAPL" }, AsOfDate, CancellationToken.None);

        Assert.Equal(0, alpacaFeeder.CallCount);
        Assert.Equal(1, tiingoFeeder.CallCount);

        // Reconciliation still ran, using the pre-existing Alpaca row plus the fresh Tiingo bar.
        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.Equal("AAPL", verification.Symbol);
        Assert.True(verification.Verified);

        // Exactly the one seeded Alpaca row exists - confirms no duplicate/second Alpaca row
        // was written by a live fetch that should never have happened.
        var alpacaRows = await dbContext.RawOhlcvBars.Where(b => b.Source == SourceType.Alpaca).ToListAsync();
        Assert.Single(alpacaRows);
    }

    [Fact]
    public async Task VerifyCandidatesAsync_NoExistingAlpacaCoverage_FallsBackToLiveAlpacaFetchAndCallsTiingo()
    {
        // No RawOhlcvBars seeded at all - a genuinely missing case (ticker wasn't in that
        // night's universe sweep, or this is being run out-of-band). Must fall back to a live
        // Alpaca fetch, same as before the 2026-08-06 fix, not fail closed.
        using var dbContext = InMemoryDbContextFactory.Create();

        var alpacaFeeder = new FakeFeeder(SourceType.Alpaca, (symbol, start, end) => new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Alpaca, symbol, 100.00m, AsOfTimestamp.AddDays(-1)) }
        });
        var tiingoFeeder = new FakeFeeder(SourceType.Tiingo, (symbol, start, end) => new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Tiingo, symbol, 100.02m, AsOfTimestamp.AddDays(-1)) }
        });

        var service = BuildService(dbContext, new IMarketDataFeeder[] { alpacaFeeder, tiingoFeeder });

        await service.VerifyCandidatesAsync(new[] { "AAPL" }, AsOfDate, CancellationToken.None);

        Assert.Equal(1, alpacaFeeder.CallCount);
        Assert.Equal(1, tiingoFeeder.CallCount);

        var verification = await dbContext.BarVerifications.SingleAsync();
        Assert.Equal("AAPL", verification.Symbol);
        Assert.True(verification.Verified);
    }

    [Fact]
    public async Task VerifyCandidatesAsync_OnlyStaleAlpacaCoverageOutsideRecencyWindow_FallsBackToLiveAlpacaFetch()
    {
        // An existing Alpaca row is present within the 10-day lookback window but older than
        // the 3-day recency tolerance - "incomplete" coverage (e.g. the sweep hasn't run in a
        // few days) per the CLAUDE.md decision. Must still be treated as insufficient and fall
        // back to a live fetch, not silently trusted as "already covered."
        using var dbContext = InMemoryDbContextFactory.Create();

        dbContext.RawOhlcvBars.Add(MakeBar(SourceType.Alpaca, "AAPL", 99.00m, AsOfTimestamp.AddDays(-8)));
        await dbContext.SaveChangesAsync();

        var alpacaFeeder = new FakeFeeder(SourceType.Alpaca, (symbol, start, end) => new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Alpaca, symbol, 100.00m, AsOfTimestamp.AddDays(-1)) }
        });
        var tiingoFeeder = new FakeFeeder(SourceType.Tiingo, (symbol, start, end) => new FeedFetchResult
        {
            Success = true,
            Bars = new[] { MakeBar(SourceType.Tiingo, symbol, 100.02m, AsOfTimestamp.AddDays(-1)) }
        });

        var service = BuildService(dbContext, new IMarketDataFeeder[] { alpacaFeeder, tiingoFeeder });

        await service.VerifyCandidatesAsync(new[] { "AAPL" }, AsOfDate, CancellationToken.None);

        Assert.Equal(1, alpacaFeeder.CallCount);
        Assert.Equal(1, tiingoFeeder.CallCount);
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

        public int CallCount { get; private set; }

        public Task<FeedFetchResult> FetchDailyBarsAsync(string symbol, DateOnly start, DateOnly end, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_resultFactory(symbol, start, end));
        }
    }
}
