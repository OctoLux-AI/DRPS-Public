using Drps.Ingestion.Feeders;
using Drps.Ingestion.Orchestration;
using Drps.Shared.Exceptions;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Orchestration;

public class ExDividendIngestionRunnerTests
{
    private static readonly DateOnly Start = new(2026, 1, 1);
    private static readonly DateOnly End = new(2026, 6, 1);

    private static RawExDividendObservation MakeObservation(SourceType source, string symbol) => new()
    {
        Source = source,
        Symbol = symbol,
        ExDividendDate = new DateOnly(2026, 5, 9),
        Value = 0.26m,
        SampleCount = 1,
        VariancePct = null,
        Verified = false,
        IngestedAt = DateTimeOffset.UtcNow,
        RequestId = Guid.NewGuid()
    };

    [Fact]
    public async Task RunAsync_FeederSucceeds_PersistsObservationsAndRecordsSourceStatus()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var feeder = new FakeExDividendFeeder(SourceType.Finnhub, new ExDividendFetchResult
        {
            Success = true,
            Observations = new[] { MakeObservation(SourceType.Finnhub, "AAPL") }
        });

        var runner = new ExDividendIngestionRunner(
            new IExDividendFeeder[] { feeder }, dbContext, tracker, NullLogger<ExDividendIngestionRunner>.Instance);

        await runner.RunAsync("AAPL", Start, End, CancellationToken.None);

        Assert.Equal(1, await dbContext.RawExDividendObservations.CountAsync());
        var status = await dbContext.SourceStatuses.SingleAsync(s => s.Source == SourceType.Finnhub);
        Assert.Equal(SourceStatusTracker.ExDividendFieldOrBarType, status.FieldOrBarType);
        Assert.Equal(1, status.MatchedObservationCount);
    }

    [Fact]
    public async Task RunAsync_FeederThrowsConfigurationMissingException_DoesNotCreateSourceStatusRow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var configErrorFeeder = new FakeExDividendFeeder(
            SourceType.Finnhub, () => throw new ConfigurationMissingException("Finnhub:ApiKey"));

        var runner = new ExDividendIngestionRunner(
            new IExDividendFeeder[] { configErrorFeeder }, dbContext, tracker, NullLogger<ExDividendIngestionRunner>.Instance);

        var exception = await Record.ExceptionAsync(() => runner.RunAsync("AAPL", Start, End, CancellationToken.None));

        Assert.Null(exception);

        // A missing Finnhub key must not count as a strike - no SourceStatus row at all,
        // same carve-out precedent as Alpaca/Tiingo in IngestionRunner.
        var sources = await dbContext.SourceStatuses.Select(s => s.Source).ToListAsync();
        Assert.DoesNotContain(SourceType.Finnhub, sources);
        Assert.Empty(await dbContext.RawExDividendObservations.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_FeederThrowsConfigurationMissingException_ExistingSourceStatusConsecutiveFailuresUntouched()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        dbContext.SourceStatuses.Add(new SourceStatus
        {
            Source = SourceType.Finnhub,
            FieldOrBarType = SourceStatusTracker.ExDividendFieldOrBarType,
            TrustState = TrustState.Candidate,
            ConsecutiveFailures = 2,
            MatchedObservationCount = 0,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await dbContext.SaveChangesAsync();

        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);
        var configErrorFeeder = new FakeExDividendFeeder(
            SourceType.Finnhub, () => throw new ConfigurationMissingException("Finnhub:ApiKey"));

        var runner = new ExDividendIngestionRunner(
            new IExDividendFeeder[] { configErrorFeeder }, dbContext, tracker, NullLogger<ExDividendIngestionRunner>.Instance);

        await runner.RunAsync("AAPL", Start, End, CancellationToken.None);

        // A configuration error must never push an already-struggling source the rest of the
        // way to Dead.
        var status = await dbContext.SourceStatuses.SingleAsync(s => s.Source == SourceType.Finnhub);
        Assert.Equal(2, status.ConsecutiveFailures);
        Assert.Equal(TrustState.Candidate, status.TrustState);
    }

    [Fact]
    public async Task RunAsync_FeederReturnsNoDataForRange_DoesNotCreateSourceStatusRow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var noDataFeeder = new FakeExDividendFeeder(SourceType.Finnhub, new ExDividendFetchResult
        {
            Success = true,
            Observations = Array.Empty<RawExDividendObservation>(),
            NoDataForRange = true
        });

        var runner = new ExDividendIngestionRunner(
            new IExDividendFeeder[] { noDataFeeder }, dbContext, tracker, NullLogger<ExDividendIngestionRunner>.Instance);

        await runner.RunAsync("NODIV", Start, End, CancellationToken.None);

        // A well-formed but empty response (never-dividend-paying stock or bad ticker,
        // ambiguous) is neither a real success nor a real failure - no RecordResultAsync
        // call at all.
        var sources = await dbContext.SourceStatuses.Select(s => s.Source).ToListAsync();
        Assert.DoesNotContain(SourceType.Finnhub, sources);
    }

    [Fact]
    public async Task RunAsync_FeederReturnsRealFailure_StillIncrementsConsecutiveFailures()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var failFeeder = new FakeExDividendFeeder(SourceType.Finnhub, new ExDividendFetchResult
        {
            Success = false,
            ErrorMessage = "401 Unauthorized"
        });

        var runner = new ExDividendIngestionRunner(
            new IExDividendFeeder[] { failFeeder }, dbContext, tracker, NullLogger<ExDividendIngestionRunner>.Instance);

        await runner.RunAsync("AAPL", Start, End, CancellationToken.None);

        // A real, attempted-and-rejected failure must still count as a strike - only the
        // "never attempted" (config error) case is carved out.
        var status = await dbContext.SourceStatuses.SingleAsync(s => s.Source == SourceType.Finnhub);
        Assert.Equal(1, status.ConsecutiveFailures);
    }

    [Fact]
    public async Task RunAsync_SourceMarkedDead_SkipsFetchEntirelyWithoutCallingFeeder()
    {
        // The actual bug this fix closes: SourceStatusTracker.RecordResultAsync marking a
        // source Dead previously had no effect on this runner - it kept calling the feeder
        // indefinitely. This asserts the feeder is never invoked at all once Dead, not just
        // that no new SourceStatus row/observations resulted.
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await tracker.RecordResultAsync(SourceType.Finnhub, SourceStatusTracker.ExDividendFieldOrBarType, success: false, CancellationToken.None);
        }
        Assert.True(await tracker.IsDeadAsync(SourceType.Finnhub, SourceStatusTracker.ExDividendFieldOrBarType, CancellationToken.None));

        var callCount = 0;
        var feeder = new FakeExDividendFeeder(SourceType.Finnhub, () =>
        {
            callCount++;
            return new ExDividendFetchResult
            {
                Success = true,
                Observations = new[] { MakeObservation(SourceType.Finnhub, "AAPL") }
            };
        });

        var runner = new ExDividendIngestionRunner(
            new IExDividendFeeder[] { feeder }, dbContext, tracker, NullLogger<ExDividendIngestionRunner>.Instance);

        await runner.RunAsync("AAPL", Start, End, CancellationToken.None);

        Assert.Equal(0, callCount);
        Assert.Empty(await dbContext.RawExDividendObservations.ToListAsync());
    }

    [Fact]
    public async Task RunAsync_SourceMarkedDead_DoesNotRecordSourceStatusForTheSkip()
    {
        // A skip is neither a success nor a failure - ConsecutiveFailures must not move in
        // either direction just from being skipped while Dead, same "never attempted"
        // treatment as the ConfigurationMissingException carve-out.
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await tracker.RecordResultAsync(SourceType.Finnhub, SourceStatusTracker.ExDividendFieldOrBarType, success: false, CancellationToken.None);
        }

        var feeder = new FakeExDividendFeeder(SourceType.Finnhub, new ExDividendFetchResult
        {
            Success = true,
            Observations = new[] { MakeObservation(SourceType.Finnhub, "AAPL") }
        });

        var runner = new ExDividendIngestionRunner(
            new IExDividendFeeder[] { feeder }, dbContext, tracker, NullLogger<ExDividendIngestionRunner>.Instance);

        await runner.RunAsync("AAPL", Start, End, CancellationToken.None);

        var status = await dbContext.SourceStatuses.SingleAsync(s => s.Source == SourceType.Finnhub);
        Assert.Equal(3, status.ConsecutiveFailures);
        Assert.Equal(TrustState.Dead, status.TrustState);
    }

    [Fact]
    public async Task RunAsync_SourceNotDead_StillFetchesNormally()
    {
        // Regression guard for the fix itself: a Candidate/Trusted source (the common case)
        // must not be accidentally skipped by the new dead-source check.
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var callCount = 0;
        var feeder = new FakeExDividendFeeder(SourceType.Finnhub, () =>
        {
            callCount++;
            return new ExDividendFetchResult
            {
                Success = true,
                Observations = new[] { MakeObservation(SourceType.Finnhub, "AAPL") }
            };
        });

        var runner = new ExDividendIngestionRunner(
            new IExDividendFeeder[] { feeder }, dbContext, tracker, NullLogger<ExDividendIngestionRunner>.Instance);

        await runner.RunAsync("AAPL", Start, End, CancellationToken.None);

        Assert.Equal(1, callCount);
        Assert.Equal(1, await dbContext.RawExDividendObservations.CountAsync());
    }

    [Fact]
    public async Task RunAsync_SourceDeadForDifferentFieldOrBarType_StillFetchesNormally()
    {
        // SourceStatus rows are keyed by (Source, FieldOrBarType) - Finnhub being Dead for a
        // different field/bar type entirely must not block its ExDividend fetches.
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await tracker.RecordResultAsync(SourceType.Finnhub, SourceStatusTracker.DailyBarFieldOrBarType, success: false, CancellationToken.None);
        }

        var callCount = 0;
        var feeder = new FakeExDividendFeeder(SourceType.Finnhub, () =>
        {
            callCount++;
            return new ExDividendFetchResult
            {
                Success = true,
                Observations = new[] { MakeObservation(SourceType.Finnhub, "AAPL") }
            };
        });

        var runner = new ExDividendIngestionRunner(
            new IExDividendFeeder[] { feeder }, dbContext, tracker, NullLogger<ExDividendIngestionRunner>.Instance);

        await runner.RunAsync("AAPL", Start, End, CancellationToken.None);

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task RunAsync_FeederThrowsDirectly_CatchesLogsAndDoesNotReportStatusForThatFeeder()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var throwingFeeder = new FakeExDividendFeeder(SourceType.Finnhub, () => throw new InvalidOperationException("boom"));

        var runner = new ExDividendIngestionRunner(
            new IExDividendFeeder[] { throwingFeeder }, dbContext, tracker, NullLogger<ExDividendIngestionRunner>.Instance);

        var exception = await Record.ExceptionAsync(() => runner.RunAsync("AAPL", Start, End, CancellationToken.None));

        Assert.Null(exception);

        // No ExDividendFetchResult was ever produced, so RecordResultAsync was never called.
        var sources = await dbContext.SourceStatuses.Select(s => s.Source).ToListAsync();
        Assert.DoesNotContain(SourceType.Finnhub, sources);
    }

    private sealed class FakeExDividendFeeder : IExDividendFeeder
    {
        private readonly Func<Task<ExDividendFetchResult>> _handler;

        public FakeExDividendFeeder(SourceType source, ExDividendFetchResult result)
            : this(source, () => Task.FromResult(result))
        {
        }

        public FakeExDividendFeeder(SourceType source, Func<ExDividendFetchResult> handler)
            : this(source, () => Task.FromResult(handler()))
        {
        }

        private FakeExDividendFeeder(SourceType source, Func<Task<ExDividendFetchResult>> handler)
        {
            Source = source;
            _handler = handler;
        }

        public SourceType Source { get; }

        public Task<ExDividendFetchResult> FetchExDividendsAsync(string symbol, DateOnly start, DateOnly end, CancellationToken cancellationToken)
            => _handler();
    }
}
