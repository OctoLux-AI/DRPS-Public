using Microsoft.Extensions.Hosting;

namespace Drps.Tests.TestHelpers;

// Hand-rolled fake, same convention as FakeHttpMessageHandler/FakeTradingCalendarService in
// this folder - no mocking library anywhere in this codebase. Only StopApplication is
// exercised by the named-run-now on-demand path under test; the three CancellationToken
// properties are never observed by any Worker, so they return CancellationToken.None rather
// than wiring up real start/stop semantics nothing here needs.
public sealed class FakeHostApplicationLifetime : IHostApplicationLifetime
{
    private int _stopApplicationCallCount;

    public int StopApplicationCallCount => _stopApplicationCallCount;

    public bool StopApplicationCalled => _stopApplicationCallCount > 0;

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() => Interlocked.Increment(ref _stopApplicationCallCount);
}
