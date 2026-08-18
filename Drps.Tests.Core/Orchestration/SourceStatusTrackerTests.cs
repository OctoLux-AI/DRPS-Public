using Drps.Ingestion.Orchestration;
using Drps.Shared.Models;
using Drps.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Drps.Tests.Orchestration;

public class SourceStatusTrackerTests
{
    private const string FieldOrBarType = SourceStatusTracker.DailyBarFieldOrBarType;

    [Fact]
    public async Task RecordResultAsync_NoExistingRow_CreatesNewCandidateRow()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: true, CancellationToken.None);

        var status = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(SourceType.Alpaca, status.Source);
        Assert.Equal(FieldOrBarType, status.FieldOrBarType);
        Assert.Equal(TrustState.Candidate, status.TrustState);
    }

    [Fact]
    public async Task RecordResultAsync_SingleSuccess_IncrementsMatchedResetsFailuresSetsLastSuccess()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: true, CancellationToken.None);

        var status = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(1, status.MatchedObservationCount);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.NotNull(status.LastSuccessAt);
    }

    [Fact]
    public async Task RecordResultAsync_FiveConsecutiveSuccesses_PromotesToTrusted()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        for (var i = 0; i < 5; i++)
        {
            await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: true, CancellationToken.None);
        }

        var status = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(TrustState.Trusted, status.TrustState);
        Assert.Equal(5, status.MatchedObservationCount);
    }

    [Fact]
    public async Task RecordResultAsync_ThreeConsecutiveFailures_MarksDead()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: false, CancellationToken.None);
        }

        var status = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(TrustState.Dead, status.TrustState);
        Assert.Equal(3, status.ConsecutiveFailures);
    }

    [Fact]
    public async Task RecordResultAsync_TrustedSourceThenThreeFailures_FlipsToDead()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        for (var i = 0; i < 5; i++)
        {
            await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: true, CancellationToken.None);
        }

        var promoted = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(TrustState.Trusted, promoted.TrustState);

        for (var i = 0; i < 3; i++)
        {
            await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: false, CancellationToken.None);
        }

        var status = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(TrustState.Dead, status.TrustState);
    }

    [Fact]
    public async Task RecordResultAsync_SuccessAfterFailures_ResetsConsecutiveFailuresSoTwoMoreFailuresDoNotKillSource()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: false, CancellationToken.None);
        await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: false, CancellationToken.None);
        await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: true, CancellationToken.None);

        var afterReset = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(0, afterReset.ConsecutiveFailures);

        // Two more failures after the reset should not carry over the pre-reset count —
        // if the reset didn't actually happen, this would reach 4 (2+2) and flip to Dead.
        await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: false, CancellationToken.None);
        await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: false, CancellationToken.None);

        var status = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(2, status.ConsecutiveFailures);
        Assert.NotEqual(TrustState.Dead, status.TrustState);
    }

    [Fact]
    public async Task RecordResultAsync_DeadSourceThenSuccess_RecoversToCandidate()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: false, CancellationToken.None);
        }

        var dead = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(TrustState.Dead, dead.TrustState);

        await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: true, CancellationToken.None);

        var recovered = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(TrustState.Candidate, recovered.TrustState);
    }

    [Fact]
    public async Task RecordResultAsync_DeadSourceThenSuccess_ResetsConsecutiveFailuresAndSetsLastSuccessAt()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: false, CancellationToken.None);
        }

        await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: true, CancellationToken.None);

        var status = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.NotNull(status.LastSuccessAt);
    }

    [Fact]
    public async Task RecordResultAsync_DeadSourceWithMatchedCountAlreadyAtThreshold_RecoveryLandsAtCandidateNotTrusted()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        // Accumulate MatchedObservationCount to the promotion threshold first...
        for (var i = 0; i < 5; i++)
        {
            await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: true, CancellationToken.None);
        }

        var trusted = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(TrustState.Trusted, trusted.TrustState);

        // ...then drive it to Dead via three consecutive failures (MatchedObservationCount
        // itself never resets on failure, so it's still 5 going into the recovery success).
        for (var i = 0; i < 3; i++)
        {
            await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: false, CancellationToken.None);
        }

        var dead = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(TrustState.Dead, dead.TrustState);

        // The recovery success must land at Candidate this call, even though
        // MatchedObservationCount (now 6) already exceeds PromotionThreshold - it must not
        // skip straight back to Trusted on the same success that revives it from Dead.
        await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: true, CancellationToken.None);

        var recovered = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(TrustState.Candidate, recovered.TrustState);
        Assert.Equal(6, recovered.MatchedObservationCount);

        // A subsequent success, now that it's sitting at Candidate again, promotes normally
        // via the existing (unchanged) Candidate->Trusted path.
        await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: true, CancellationToken.None);

        var repromoted = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(TrustState.Trusted, repromoted.TrustState);
        Assert.Equal(7, repromoted.MatchedObservationCount);
    }

    [Fact]
    public async Task RecordResultAsync_TrustedSourceThenSuccess_UnaffectedByDeadRecoveryBranch()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        for (var i = 0; i < 5; i++)
        {
            await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: true, CancellationToken.None);
        }

        var trusted = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(TrustState.Trusted, trusted.TrustState);

        // A source that is not Dead must be completely untouched by the new branch - still
        // Trusted, not reset back to Candidate.
        await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: true, CancellationToken.None);

        var status = await dbContext.SourceStatuses.SingleAsync();
        Assert.Equal(TrustState.Trusted, status.TrustState);
    }

    [Fact]
    public async Task RecordResultAsync_EachCall_UpdatesUpdatedAtToALaterTimestamp()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: true, CancellationToken.None);
        var firstUpdatedAt = (await dbContext.SourceStatuses.SingleAsync()).UpdatedAt;

        await Task.Delay(5);

        await tracker.RecordResultAsync(SourceType.Alpaca, FieldOrBarType, success: true, CancellationToken.None);
        var secondUpdatedAt = (await dbContext.SourceStatuses.SingleAsync()).UpdatedAt;

        Assert.True(secondUpdatedAt > firstUpdatedAt);
    }

    [Fact]
    public async Task IsDeadAsync_NoExistingRow_ReturnsFalse()
    {
        // A source with no recorded history has never failed - nothing to gate.
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        var isDead = await tracker.IsDeadAsync(SourceType.Finnhub, FieldOrBarType, CancellationToken.None);

        Assert.False(isDead);
    }

    [Fact]
    public async Task IsDeadAsync_CandidateOrTrustedSource_ReturnsFalse()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        await tracker.RecordResultAsync(SourceType.Finnhub, FieldOrBarType, success: true, CancellationToken.None);

        var isDead = await tracker.IsDeadAsync(SourceType.Finnhub, FieldOrBarType, CancellationToken.None);

        Assert.False(isDead);
    }

    [Fact]
    public async Task IsDeadAsync_ThreeConsecutiveFailures_ReturnsTrue()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await tracker.RecordResultAsync(SourceType.Finnhub, FieldOrBarType, success: false, CancellationToken.None);
        }

        var isDead = await tracker.IsDeadAsync(SourceType.Finnhub, FieldOrBarType, CancellationToken.None);

        Assert.True(isDead);
    }

    [Fact]
    public async Task IsDeadAsync_DeadSourceRecoversAfterSuccess_ReturnsFalse()
    {
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await tracker.RecordResultAsync(SourceType.Finnhub, FieldOrBarType, success: false, CancellationToken.None);
        }
        Assert.True(await tracker.IsDeadAsync(SourceType.Finnhub, FieldOrBarType, CancellationToken.None));

        await tracker.RecordResultAsync(SourceType.Finnhub, FieldOrBarType, success: true, CancellationToken.None);

        Assert.False(await tracker.IsDeadAsync(SourceType.Finnhub, FieldOrBarType, CancellationToken.None));
    }

    [Fact]
    public async Task IsDeadAsync_ScopedPerFieldOrBarType_DeadOnOneFieldDoesNotAffectAnother()
    {
        // Finnhub can be Dead for ExDividend while still Candidate/Trusted for a different
        // field/bar type entirely - SourceStatus rows are keyed by (Source, FieldOrBarType),
        // not by Source alone.
        using var dbContext = InMemoryDbContextFactory.Create();
        var tracker = new SourceStatusTracker(dbContext, NullLogger<SourceStatusTracker>.Instance);

        for (var i = 0; i < 3; i++)
        {
            await tracker.RecordResultAsync(SourceType.Finnhub, SourceStatusTracker.ExDividendFieldOrBarType, success: false, CancellationToken.None);
        }

        Assert.True(await tracker.IsDeadAsync(SourceType.Finnhub, SourceStatusTracker.ExDividendFieldOrBarType, CancellationToken.None));
        Assert.False(await tracker.IsDeadAsync(SourceType.Finnhub, SourceStatusTracker.DailyBarFieldOrBarType, CancellationToken.None));
    }
}
