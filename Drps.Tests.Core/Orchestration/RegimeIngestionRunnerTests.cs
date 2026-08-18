using Drps.Ingestion.Feeders;
using Drps.Ingestion.Orchestration;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Orchestration;

public class RegimeIngestionRunnerTests
{
    private static readonly DateOnly RunDate = new(2026, 7, 27);

    private static RawRegimeObservation Observation(string ticker, DateOnly date, decimal close, RegimeSourceType source) =>
        new() { Ticker = ticker, ObservationDate = date, Close = close, Source = source, FetchedAt = DateTime.UtcNow };

    [Fact]
    public async Task RunAsync_NoExistingRows_PersistsEveryObservationFromEveryFeeder()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var vix = new FakeRegimeFeeder("VIX", RegimeSourceType.CboeDirect, success: true, observations:
        [
            Observation("VIX", new DateOnly(2026, 7, 23), 17.67m, RegimeSourceType.CboeDirect),
            Observation("VIX", new DateOnly(2026, 7, 24), 18.58m, RegimeSourceType.CboeDirect)
        ]);
        var runner = new RegimeIngestionRunner([vix], dbContext, NullLogger<RegimeIngestionRunner>.Instance);

        await runner.RunAsync(RunDate, CancellationToken.None);

        var rows = await dbContext.RawRegimeObservations.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1, vix.CallCount);
    }

    [Fact]
    public async Task RunAsync_SomeDatesAlreadyStored_OnlyInsertsStrictlyNewerObservations()
    {
        // The core reason this runner exists instead of blindly AddRange-ing like
        // SectorIngestionRunner: Cboe/FRED return the ENTIRE history on every call, so a
        // naive append would re-insert thousands of already-known rows every single run.
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawRegimeObservations.Add(Observation("VIX", new DateOnly(2026, 7, 23), 17.67m, RegimeSourceType.CboeDirect));
        await dbContext.SaveChangesAsync();

        var vix = new FakeRegimeFeeder("VIX", RegimeSourceType.CboeDirect, success: true, observations:
        [
            Observation("VIX", new DateOnly(2026, 7, 22), 17.42m, RegimeSourceType.CboeDirect),
            Observation("VIX", new DateOnly(2026, 7, 23), 17.67m, RegimeSourceType.CboeDirect),
            Observation("VIX", new DateOnly(2026, 7, 24), 18.58m, RegimeSourceType.CboeDirect)
        ]);
        var runner = new RegimeIngestionRunner([vix], dbContext, NullLogger<RegimeIngestionRunner>.Instance);

        await runner.RunAsync(RunDate, CancellationToken.None);

        var rows = await dbContext.RawRegimeObservations.ToListAsync();
        // The pre-existing 7/23 row plus exactly one new row (7/24) - 7/22 (older than the
        // latest stored date) and the re-fetched 7/23 (not strictly newer) must both be
        // skipped.
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.ObservationDate == new DateOnly(2026, 7, 24));
        Assert.DoesNotContain(rows, r => r.ObservationDate == new DateOnly(2026, 7, 22));
    }

    [Fact]
    public async Task RunAsync_DedupIsScopedPerTickerAndSource_DoesNotCrossContaminateDifferentSources()
    {
        // FRED and Cboe direct rows for the same ticker/date are both legitimately persisted
        // (no cross-source reconciliation in this task) - the incremental-insert dedup must
        // key on (Ticker, Source), not Ticker alone, or a Cboe row would incorrectly suppress
        // a FRED row for the same date.
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.RawRegimeObservations.Add(Observation("VXN", new DateOnly(2026, 7, 24), 28.39m, RegimeSourceType.CboeDirect));
        await dbContext.SaveChangesAsync();

        var vxnFred = new FakeRegimeFeeder("VXN", RegimeSourceType.Fred, success: true, observations:
        [
            Observation("VXN", new DateOnly(2026, 7, 24), 28.39m, RegimeSourceType.Fred)
        ]);
        var runner = new RegimeIngestionRunner([vxnFred], dbContext, NullLogger<RegimeIngestionRunner>.Instance);

        await runner.RunAsync(RunDate, CancellationToken.None);

        var rows = await dbContext.RawRegimeObservations.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Source == RegimeSourceType.CboeDirect);
        Assert.Contains(rows, r => r.Source == RegimeSourceType.Fred);
    }

    [Fact]
    public async Task RunAsync_OneFeederFailsAnotherSucceeds_IsolatesFailureAndPersistsSurvivor()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var failingFeeder = new FakeRegimeFeeder("VIX3M", RegimeSourceType.Fred, success: false, observations: [], errorMessage: "boom");
        var succeedingFeeder = new FakeRegimeFeeder("VIX", RegimeSourceType.CboeDirect, success: true, observations:
        [
            Observation("VIX", new DateOnly(2026, 7, 24), 18.58m, RegimeSourceType.CboeDirect)
        ]);
        var runner = new RegimeIngestionRunner([failingFeeder, succeedingFeeder], dbContext, NullLogger<RegimeIngestionRunner>.Instance);

        await runner.RunAsync(RunDate, CancellationToken.None);

        var rows = await dbContext.RawRegimeObservations.ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("VIX", row.Ticker);
    }

    [Fact]
    public async Task RunAsync_FeederThrowsUnexpectedly_IsolatesFailureAndContinuesToNextFeeder()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var throwingFeeder = new FakeRegimeFeeder("VXN", RegimeSourceType.CboeDirect, throwException: true);
        var succeedingFeeder = new FakeRegimeFeeder("VIX", RegimeSourceType.CboeDirect, success: true, observations:
        [
            Observation("VIX", new DateOnly(2026, 7, 24), 18.58m, RegimeSourceType.CboeDirect)
        ]);
        var runner = new RegimeIngestionRunner([throwingFeeder, succeedingFeeder], dbContext, NullLogger<RegimeIngestionRunner>.Instance);

        var exception = await Record.ExceptionAsync(() => runner.RunAsync(RunDate, CancellationToken.None));

        Assert.Null(exception);
        var row = Assert.Single(await dbContext.RawRegimeObservations.ToListAsync());
        Assert.Equal("VIX", row.Ticker);
    }

    [Fact]
    public async Task RunAsync_AlreadyRanTodayPerGuard_SkipsFetchEntirely()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        await WorkerRunGuard.RecordSuccessfulRunAsync(
            dbContext, "Drps.Regime", RunDate, new DateTime(2026, 7, 27, 3, 0, 0), CancellationToken.None);

        var vix = new FakeRegimeFeeder("VIX", RegimeSourceType.CboeDirect, success: true, observations:
        [
            Observation("VIX", new DateOnly(2026, 7, 24), 18.58m, RegimeSourceType.CboeDirect)
        ]);
        var runner = new RegimeIngestionRunner([vix], dbContext, NullLogger<RegimeIngestionRunner>.Instance);

        await runner.RunAsync(RunDate, CancellationToken.None);

        Assert.Equal(0, vix.CallCount);
        Assert.Empty(await dbContext.RawRegimeObservations.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_CompletesRun_RecordsSuccessInGuard()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var vix = new FakeRegimeFeeder("VIX", RegimeSourceType.CboeDirect, success: true, observations:
        [
            Observation("VIX", new DateOnly(2026, 7, 24), 18.58m, RegimeSourceType.CboeDirect)
        ]);
        var runner = new RegimeIngestionRunner([vix], dbContext, NullLogger<RegimeIngestionRunner>.Instance);

        await runner.RunAsync(RunDate, CancellationToken.None);

        var hasRun = await WorkerRunGuard.HasRunTodayAsync(dbContext, "Drps.Regime", RunDate, CancellationToken.None);
        Assert.True(hasRun);
    }

    [Fact]
    public async Task RunAsync_AllFeedersSucceed_RunRecordReflectsFullSuccessWithNoFailureDetail()
    {
        // CLAUDE.md's "Regime Ingestion: Partial-Success Guard Gap" (2026-07-27), part (a): a
        // genuinely clean run must still be recorded as a clean success, not just "not a
        // failure" - AllSourcesSucceeded must be explicitly true and FailureDetail explicitly
        // null, not merely absent from the row.
        using var dbContext = InMemoryDbContextFactory.Create();
        var vix = new FakeRegimeFeeder("VIX", RegimeSourceType.CboeDirect, success: true, observations:
        [
            Observation("VIX", new DateOnly(2026, 7, 24), 18.58m, RegimeSourceType.CboeDirect)
        ]);
        var vxnFred = new FakeRegimeFeeder("VXN", RegimeSourceType.Fred, success: true, observations:
        [
            Observation("VXN", new DateOnly(2026, 7, 24), 28.39m, RegimeSourceType.Fred)
        ]);
        var runner = new RegimeIngestionRunner([vix, vxnFred], dbContext, NullLogger<RegimeIngestionRunner>.Instance);

        await runner.RunAsync(RunDate, CancellationToken.None);

        var record = Assert.Single(await dbContext.WorkerRunRecords.ToListAsync());
        Assert.True(record.AllSourcesSucceeded);
        Assert.Null(record.FailureDetail);
    }

    [Fact]
    public async Task RunAsync_Vix3MFredTransportFailure_RunRecordIsDistinctFromFullSuccess()
    {
        // The exact scenario confirmed live 2026-07-27: VIX/Cboe, VXN/Cboe, VXN/Fred, and
        // VIX3M/Cboe all succeed; VIX3M/Fred fails after retries are exhausted. Before this
        // fix, the completion row this run produced was indistinguishable from a fully clean
        // run - this test proves that gap is closed: a single failed source must flip
        // AllSourcesSucceeded to false and name the failing source in FailureDetail, even
        // though the run as a whole still reaches WorkerRunGuard.RecordSuccessfulRunAsync
        // (same-day retry behavior is unchanged/out of scope for this fix).
        using var dbContext = InMemoryDbContextFactory.Create();
        var vix = new FakeRegimeFeeder("VIX", RegimeSourceType.CboeDirect, success: true, observations:
        [
            Observation("VIX", new DateOnly(2026, 7, 24), 18.58m, RegimeSourceType.CboeDirect)
        ]);
        var vxnCboe = new FakeRegimeFeeder("VXN", RegimeSourceType.CboeDirect, success: true, observations:
        [
            Observation("VXN", new DateOnly(2026, 7, 24), 28.39m, RegimeSourceType.CboeDirect)
        ]);
        var vxnFred = new FakeRegimeFeeder("VXN", RegimeSourceType.Fred, success: true, observations:
        [
            Observation("VXN", new DateOnly(2026, 7, 24), 28.39m, RegimeSourceType.Fred)
        ]);
        var vix3MCboe = new FakeRegimeFeeder("VIX3M", RegimeSourceType.CboeDirect, success: true, observations:
        [
            Observation("VIX3M", new DateOnly(2026, 7, 24), 19.12m, RegimeSourceType.CboeDirect)
        ]);
        var vix3MFredTransportFailure = new FakeRegimeFeeder(
            "VIX3M", RegimeSourceType.Fred, success: false, observations: [],
            errorMessage: "transport-level failure after 3 retries (timeout)");
        var runner = new RegimeIngestionRunner(
            [vix, vxnCboe, vxnFred, vix3MCboe, vix3MFredTransportFailure], dbContext, NullLogger<RegimeIngestionRunner>.Instance);

        await runner.RunAsync(RunDate, CancellationToken.None);

        var record = Assert.Single(await dbContext.WorkerRunRecords.ToListAsync());
        Assert.False(record.AllSourcesSucceeded);
        Assert.NotNull(record.FailureDetail);
        Assert.Contains("VIX3M", record.FailureDetail);
        Assert.Contains(RegimeSourceType.Fred.ToString(), record.FailureDetail);
        // The idempotency guard itself is unaffected by this fix - a partial failure still
        // reaches RecordSuccessfulRunAsync, so a same-day retry still skips (see CLAUDE.md's
        // own "wait for tomorrow" workaround note for why same-day retry redesign is explicitly
        // out of scope here).
        var hasRun = await WorkerRunGuard.HasRunTodayAsync(dbContext, "Drps.Regime", RunDate, CancellationToken.None);
        Assert.True(hasRun);
    }

    private sealed class FakeRegimeFeeder : IRegimeFeeder
    {
        private readonly bool _success;
        private readonly IReadOnlyList<RawRegimeObservation> _observations;
        private readonly string? _errorMessage;
        private readonly bool _throwException;

        public FakeRegimeFeeder(
            string ticker, RegimeSourceType source,
            bool success = true, IReadOnlyList<RawRegimeObservation>? observations = null,
            string? errorMessage = null, bool throwException = false)
        {
            Ticker = ticker;
            Source = source;
            _success = success;
            _observations = observations ?? Array.Empty<RawRegimeObservation>();
            _errorMessage = errorMessage;
            _throwException = throwException;
        }

        public string Ticker { get; }

        public RegimeSourceType Source { get; }

        public int CallCount { get; private set; }

        public Task<RegimeFetchResult> FetchHistoryAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            if (_throwException)
                throw new InvalidOperationException("simulated unexpected feeder failure");

            return Task.FromResult(new RegimeFetchResult
            {
                Success = _success,
                Observations = _observations,
                ErrorMessage = _errorMessage
            });
        }
    }
}
