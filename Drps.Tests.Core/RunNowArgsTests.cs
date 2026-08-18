using Drps.Shared.Scheduling;

namespace Drps.Tests;

public class RunNowArgsTests
{
    [Fact]
    public void IsBareRunNow_BareFlagPresent_ReturnsTrue()
    {
        Assert.True(RunNowArgs.IsBareRunNow(new[] { "--run-now" }));
    }

    [Fact]
    public void IsBareRunNow_NoArgs_ReturnsFalse()
    {
        Assert.False(RunNowArgs.IsBareRunNow(null));
        Assert.False(RunNowArgs.IsBareRunNow(Array.Empty<string>()));
    }

    [Fact]
    public void IsBareRunNow_NamedFlagOnly_ReturnsFalse()
    {
        // Bare and named are mutually exclusive - a named invocation must not also satisfy the
        // bare check, even though it starts with the same "--run-now" prefix.
        Assert.False(RunNowArgs.IsBareRunNow(new[] { "--run-now=Drps.Regime" }));
    }

    [Fact]
    public void IsNamedRunNow_ExactMatch_ReturnsTrue()
    {
        Assert.True(RunNowArgs.IsNamedRunNow(new[] { "--run-now=Drps.Regime" }, "Drps.Regime"));
    }

    [Fact]
    public void IsNamedRunNow_DifferentWorkerName_ReturnsFalse()
    {
        Assert.False(RunNowArgs.IsNamedRunNow(new[] { "--run-now=Drps.Regime" }, "Drps.Sector"));
    }

    [Fact]
    public void IsNamedRunNow_BareFlagOnly_ReturnsFalse()
    {
        Assert.False(RunNowArgs.IsNamedRunNow(new[] { "--run-now" }, "Drps.Ingestion"));
    }

    [Fact]
    public void IsNamedRunNow_NoArgs_ReturnsFalse()
    {
        Assert.False(RunNowArgs.IsNamedRunNow(null, "Drps.Ingestion"));
        Assert.False(RunNowArgs.IsNamedRunNow(Array.Empty<string>(), "Drps.Ingestion"));
    }

    [Fact]
    public void IsNamedRunNow_CaseSensitiveMismatch_ReturnsFalse()
    {
        // Exact match only, per this class's own doc comment - a case-mismatched value must
        // never accidentally target a worker.
        Assert.False(RunNowArgs.IsNamedRunNow(new[] { "--run-now=drps.regime" }, "Drps.Regime"));
    }
}
