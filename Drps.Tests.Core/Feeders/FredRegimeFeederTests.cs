using Drps.Ingestion.Feeders;
using Drps.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Feeders;

public class FredRegimeFeederTests
{
    // Real FRED fredgraph.csv?id=VXNCLS shape, confirmed empirically 2026-07-26: header
    // "observation_date,VXNCLS", date as yyyy-MM-dd, and a real holiday row (2001-02-19,
    // Presidents Day) carrying nothing after the trailing comma.
    private const string FixtureWithHolidayRow =
        "observation_date,VXNCLS\n" +
        "2001-02-16,55.34\n" +
        "2001-02-19,\n" +
        "2001-02-20,57.93\n";

    private static FredRegimeFeeder CreateFeeder(
        FakeFredCsvTransport transport, string ticker = "VXN", string? url = null, string seriesId = "VXNCLS") =>
        new(transport, ticker, url ?? FredRegimeFeeder.VxnUrl, seriesId, NullLogger<FredRegimeFeeder>.Instance);

    [Fact]
    public async Task FetchHistoryAsync_SuccessfulResponse_SkipsHolidayRowAndParsesCloseOnlyWithFredSource()
    {
        var transport = FakeFredCsvTransport.ReturningCsv(FixtureWithHolidayRow);
        var feeder = CreateFeeder(transport);

        var result = await feeder.FetchHistoryAsync(CancellationToken.None);

        Assert.True(result.Success);
        // Three data rows, one of which (2001-02-19) is a holiday - only two real
        // observations should ever be produced.
        Assert.Equal(2, result.Observations.Count);
        Assert.DoesNotContain(result.Observations, o => o.ObservationDate == new DateOnly(2001, 2, 19));

        var first = result.Observations[0];
        Assert.Equal("VXN", first.Ticker);
        Assert.Equal(RegimeSourceType.Fred, first.Source);
        Assert.Equal(new DateOnly(2001, 2, 16), first.ObservationDate);
        Assert.Equal(55.34m, first.Close);
        // FRED is close-only - Open/High/Low must be null, never fabricated as same-as-close.
        Assert.Null(first.Open);
        Assert.Null(first.High);
        Assert.Null(first.Low);

        var second = result.Observations[1];
        Assert.Equal(new DateOnly(2001, 2, 20), second.ObservationDate);
        Assert.Equal(57.93m, second.Close);
    }

    [Fact]
    public async Task FetchHistoryAsync_DotPlaceholderValue_IsAlsoTreatedAsMissingNotAsError()
    {
        const string fixtureWithDot =
            "observation_date,VXNCLS\n" +
            "2001-02-16,55.34\n" +
            "2001-02-19,.\n" +
            "2001-02-20,57.93\n";
        var transport = FakeFredCsvTransport.ReturningCsv(fixtureWithDot);
        var feeder = CreateFeeder(transport);

        var result = await feeder.FetchHistoryAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Observations.Count);
    }

    [Fact]
    public async Task FetchHistoryAsync_UsesTickerUrlAndSeriesIdSuppliedAtConstruction()
    {
        const string vix3mFixture =
            "observation_date,VXVCLS\n" +
            "2007-12-04,24.65\n";
        var transport = FakeFredCsvTransport.ReturningCsv(vix3mFixture);
        var feeder = CreateFeeder(transport, "VIX3M", FredRegimeFeeder.Vix3mUrl, "VXVCLS");

        var result = await feeder.FetchHistoryAsync(CancellationToken.None);

        Assert.True(result.Success);
        var observation = Assert.Single(result.Observations);
        Assert.Equal("VIX3M", observation.Ticker);
        Assert.Equal(FredRegimeFeeder.Vix3mUrl, transport.LastRequestedUrl);
    }

    [Fact]
    public async Task FetchHistoryAsync_UnexpectedHeader_ReturnsFailureWithoutThrowing()
    {
        // Wrong series id in the header - e.g. a misconfigured URL/seriesId pairing, or FRED
        // changing its export format - must be caught as contract drift, not silently parsed.
        const string wrongSeriesFixture =
            "observation_date,SOMETHINGELSE\n" +
            "2001-02-16,55.34\n";
        var transport = FakeFredCsvTransport.ReturningCsv(wrongSeriesFixture);
        var feeder = CreateFeeder(transport);

        var result = await feeder.FetchHistoryAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Empty(result.Observations);
    }

    [Fact]
    public async Task FetchHistoryAsync_MalformedRow_IsSkippedWithoutFailingWholeFetch()
    {
        const string fixtureWithBadRow =
            "observation_date,VXNCLS\n" +
            "2001-02-16,55.34\n" +
            "not-a-real-row-at-all\n" +
            "2001-02-20,57.93\n";
        var transport = FakeFredCsvTransport.ReturningCsv(fixtureWithBadRow);
        var feeder = CreateFeeder(transport);

        var result = await feeder.FetchHistoryAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, result.Observations.Count);
    }

    // Replaces the pre-curl "NonRetryableClientError" (404) test - IFredCsvTransport has no
    // HTTP-status-code concept to distinguish a 404 from a 5xx the way the old
    // IHttpClientFactory-based path could (FeederRetryPolicy.IsTransient). A single
    // non-retryable transport failure (e.g. curl.exe exiting non-zero once, then a caller
    // simply not retrying at that layer) must still surface as Success=false without
    // throwing out of FetchHistoryAsync - covered directly by the always-fails case below,
    // which also proves the retry-then-give-up path terminates in a clean failure result
    // rather than an unhandled exception.
    [Fact]
    public async Task FetchHistoryAsync_TransportThrowsOnce_StillSucceedsAfterRetry()
    {
        var transport = FakeFredCsvTransport.FailingThenSucceeding(failCount: 1, FixtureWithHolidayRow);
        var feeder = CreateFeeder(transport);

        var result = await feeder.FetchHistoryAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, transport.CallCount);
    }

    // Proves the fetch path handles a persistent FRED failure correctly regardless of which
    // HTTP mechanism is used underneath IFredCsvTransport - this test only knows about the
    // interface, not curl.exe or HttpClient. CLAUDE.md's 2026-07-28 "Production
    // FredRegimeFeeder curl.exe Fix" task requirement: a transport-agnostic failure-handling
    // test, not one coupled to a specific transport's exception shape.
    [Fact]
    public async Task FetchHistoryAsync_PersistentTransportFailure_RetriesThreeTimesThenReturnsFailureWithoutThrowing()
    {
        var transport = FakeFredCsvTransport.AlwaysThrowing(
            new InvalidOperationException("curl.exe exited 28 fetching https://fred.stlouisfed.org/...: timed out"));
        var feeder = CreateFeeder(transport);

        var result = await feeder.FetchHistoryAsync(CancellationToken.None);

        Assert.Equal(4, transport.CallCount); // 1 initial attempt + 3 retries
        Assert.False(result.Success);
        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Contains("timed out", result.ErrorMessage);
    }

    // Fake, not a mock - matches this codebase's no-mocking-library convention. Deliberately
    // shaped around IFredCsvTransport's single method rather than any concrete transport
    // (curl.exe or HttpClient), so this fixture stays valid regardless of which one
    // FredRegimeFeeder is wired to in production.
    private sealed class FakeFredCsvTransport : IFredCsvTransport
    {
        private readonly Func<Task<string>> _behavior;

        private FakeFredCsvTransport(Func<Task<string>> behavior) => _behavior = behavior;

        public int CallCount { get; private set; }

        public string? LastRequestedUrl { get; private set; }

        public static FakeFredCsvTransport ReturningCsv(string csv) =>
            new(() => Task.FromResult(csv));

        public static FakeFredCsvTransport AlwaysThrowing(Exception exception) =>
            new(() => throw exception);

        public static FakeFredCsvTransport FailingThenSucceeding(int failCount, string csv)
        {
            var remainingFailures = failCount;
            return new FakeFredCsvTransport(() =>
            {
                if (remainingFailures > 0)
                {
                    remainingFailures--;
                    throw new InvalidOperationException("simulated transient curl.exe failure");
                }
                return Task.FromResult(csv);
            });
        }

        public Task<string> FetchCsvAsync(string url, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestedUrl = url;
            return _behavior();
        }
    }
}
