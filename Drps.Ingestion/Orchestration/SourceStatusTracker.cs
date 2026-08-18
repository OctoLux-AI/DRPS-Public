using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Drps.Ingestion.Orchestration;

public class SourceStatusTracker
{
    public const string DailyBarFieldOrBarType = "DailyBar";
    public const string ExDividendFieldOrBarType = "ExDividend";

    private const int PromotionThreshold = 5;
    private const int DeadSourceThreshold = 3;

    private readonly DrpsDbContext _dbContext;
    private readonly ILogger<SourceStatusTracker> _logger;

    public SourceStatusTracker(DrpsDbContext dbContext, ILogger<SourceStatusTracker> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    // The read half of the 3-strikes-dead rule (CLAUDE.md's "Dead source rule") - RecordResultAsync
    // above is the only thing that ever writes TrustState.Dead; this is the corresponding check a
    // caller must run before attempting a fetch, so a source marked Dead is actually skipped
    // ("stop querying it," per that rule's own wording) rather than merely recorded as unreliable
    // while still being called indefinitely. No existing row is treated as not-dead (a source with
    // no recorded history has never failed, so there is nothing to gate).
    public async Task<bool> IsDeadAsync(SourceType source, string fieldOrBarType, CancellationToken cancellationToken)
    {
        var status = await _dbContext.SourceStatuses
            .SingleOrDefaultAsync(s => s.Source == source && s.FieldOrBarType == fieldOrBarType, cancellationToken);

        return status?.TrustState == TrustState.Dead;
    }

    public async Task RecordResultAsync(SourceType source, string fieldOrBarType, bool success, CancellationToken cancellationToken)
    {
        var status = await _dbContext.SourceStatuses
            .SingleOrDefaultAsync(s => s.Source == source && s.FieldOrBarType == fieldOrBarType, cancellationToken);

        if (status is null)
        {
            status = new SourceStatus
            {
                Source = source,
                FieldOrBarType = fieldOrBarType,
                TrustState = TrustState.Candidate,
                ConsecutiveFailures = 0,
                MatchedObservationCount = 0
            };
            _dbContext.SourceStatuses.Add(status);
        }

        var now = DateTimeOffset.UtcNow;

        if (success)
        {
            status.ConsecutiveFailures = 0;
            status.MatchedObservationCount++;
            status.LastSuccessAt = now;

            // One clean success is sufficient to recover from Dead - it re-enters at
            // Candidate rather than skipping straight back to Trusted, even if
            // MatchedObservationCount already met the promotion threshold from before this
            // source went Dead. That's an else-if on purpose: it must earn Trusted again on
            // a subsequent success via the normal path below, not this same one.
            if (status.TrustState == TrustState.Dead)
            {
                status.TrustState = TrustState.Candidate;
                _logger.LogInformation(
                    "[SOURCE-STATUS]: {Source}/{FieldOrBarType} recovered from Dead to Candidate after a clean success",
                    source, fieldOrBarType);
            }
            else if (status.TrustState == TrustState.Candidate && status.MatchedObservationCount >= PromotionThreshold)
            {
                status.TrustState = TrustState.Trusted;
                _logger.LogInformation(
                    "[SOURCE-STATUS]: {Source}/{FieldOrBarType} promoted to Trusted after {Count} matched observations",
                    source, fieldOrBarType, status.MatchedObservationCount);
            }
        }
        else
        {
            status.ConsecutiveFailures++;
            status.LastFailureAt = now;

            if (status.ConsecutiveFailures >= DeadSourceThreshold)
            {
                status.TrustState = TrustState.Dead;
                _logger.LogWarning(
                    "[SOURCE-STATUS]: {Source}/{FieldOrBarType} marked Dead after {Count} consecutive failures",
                    source, fieldOrBarType, status.ConsecutiveFailures);
            }
        }

        status.UpdatedAt = now;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
