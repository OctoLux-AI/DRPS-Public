using Drps.Ingestion.Orchestration;
using Drps.Tests.TestHelpers;

namespace Drps.Tests.Orchestration;

public class WorkerRunGuardTests
{
    private const string WorkerName = "Drps.Ingestion";

    [Fact]
    public async Task HasRunTodayAsync_NoRecordExists_ReturnsFalse()
    {
        using var dbContext = InMemoryDbContextFactory.Create();

        var hasRun = await WorkerRunGuard.HasRunTodayAsync(dbContext, WorkerName, new DateOnly(2026, 7, 20), CancellationToken.None);

        Assert.False(hasRun);
    }

    [Fact]
    public async Task HasRunTodayAsync_AfterRecordingSuccessForToday_ReturnsTrue()
    {
        // Directly models the backward-clock re-fire scenario from this session's audit: a
        // successful run already happened for today (whether earlier this process's own
        // lifetime, or a prior process instance), so a second attempt for the exact same
        // calendar date must be recognized and skipped.
        using var dbContext = InMemoryDbContextFactory.Create();
        var today = new DateOnly(2026, 7, 20);

        await WorkerRunGuard.RecordSuccessfulRunAsync(dbContext, WorkerName, today, new DateTime(2026, 7, 20, 3, 0, 0), CancellationToken.None);

        var hasRun = await WorkerRunGuard.HasRunTodayAsync(dbContext, WorkerName, today, CancellationToken.None);

        Assert.True(hasRun);
    }

    [Fact]
    public async Task HasRunTodayAsync_RecordExistsForADifferentDate_ReturnsFalseForToday()
    {
        // The other half of the same claim: a marker from a PRIOR day must never block today's
        // legitimate run - "already ran" is scoped to the exact calendar date, not "ever ran."
        using var dbContext = InMemoryDbContextFactory.Create();
        var yesterday = new DateOnly(2026, 7, 19);
        var today = new DateOnly(2026, 7, 20);

        await WorkerRunGuard.RecordSuccessfulRunAsync(dbContext, WorkerName, yesterday, new DateTime(2026, 7, 19, 3, 0, 0), CancellationToken.None);

        var hasRunToday = await WorkerRunGuard.HasRunTodayAsync(dbContext, WorkerName, today, CancellationToken.None);

        Assert.False(hasRunToday);
    }

    [Fact]
    public async Task HasRunTodayAsync_RecordExistsForADifferentWorker_ReturnsFalseForThisWorker()
    {
        // WorkerRunRecord is scoped per (WorkerName, RunDate) - Drps.Calculator having already
        // run today must never cause Drps.Ingestion's own guard check to skip its own run.
        using var dbContext = InMemoryDbContextFactory.Create();
        var today = new DateOnly(2026, 7, 20);

        await WorkerRunGuard.RecordSuccessfulRunAsync(dbContext, "Drps.Calculator", today, new DateTime(2026, 7, 20, 3, 20, 0), CancellationToken.None);

        var hasRunForIngestion = await WorkerRunGuard.HasRunTodayAsync(dbContext, WorkerName, today, CancellationToken.None);

        Assert.False(hasRunForIngestion);
    }

    [Fact]
    public async Task RecordSuccessfulRunAsync_NoOutcomeArgumentsPassed_DefaultsToFullSuccessWithNoDetail()
    {
        // Every pre-existing caller (the three Worker classes, WeeklyVarianceAuditWorker,
        // UniverseIngestionRunner, UniverseBarSweepRunner) calls the original 5-argument
        // overload - none of them have a per-source partial-failure concept to report, so this
        // proves they remain unaffected by the new optional parameters added for
        // RegimeIngestionRunner (CLAUDE.md's "Regime Ingestion: Partial-Success Guard Gap",
        // 2026-07-27).
        using var dbContext = InMemoryDbContextFactory.Create();
        var today = new DateOnly(2026, 7, 20);

        await WorkerRunGuard.RecordSuccessfulRunAsync(dbContext, WorkerName, today, new DateTime(2026, 7, 20, 3, 0, 0), CancellationToken.None);

        var record = Assert.Single(dbContext.WorkerRunRecords);
        Assert.True(record.AllSourcesSucceeded);
        Assert.Null(record.FailureDetail);
    }

    [Fact]
    public async Task RecordSuccessfulRunAsync_AllSourcesSucceededExplicitlyFalse_PersistsFailureStateAndDetail()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var today = new DateOnly(2026, 7, 20);

        await WorkerRunGuard.RecordSuccessfulRunAsync(
            dbContext, WorkerName, today, new DateTime(2026, 7, 20, 3, 0, 0), CancellationToken.None,
            allSourcesSucceeded: false, failureDetail: "VIX3M/Fred: boom");

        var record = Assert.Single(dbContext.WorkerRunRecords);
        Assert.False(record.AllSourcesSucceeded);
        Assert.Equal("VIX3M/Fred: boom", record.FailureDetail);
    }
}
