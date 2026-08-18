using Drps.Ingestion.Persistence;
using Drps.Shared.Models;
using Drps.Shared.Scoring;
using Microsoft.EntityFrameworkCore;

namespace Drps.Execution.PreFire;

// Same shape and reasoning as Drps.Ingestion.Orchestration.SourceStatusTracker: find-or-create
// one row keyed by the reset boundary, mutate, save - a mutable state tracker, not append-only
// raw data (see KillSwitchCounter's own doc comment).
public class KillSwitchTracker
{
    private readonly DrpsDbContext _dbContext;
    private readonly ILogger<KillSwitchTracker> _logger;

    public KillSwitchTracker(DrpsDbContext dbContext, ILogger<KillSwitchTracker> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    // Returns true (and records the attempt) if today's trading-day count is still under
    // maxOpensPerDay; returns false (kill switch tripped) without incrementing further once
    // the cap is reached - the counter holds steady at the threshold rather than growing
    // unbounded under a genuine runaway-loop scenario.
    public async Task<bool> TryRecordOpenAttemptAsync(DateTime asOf, int maxOpensPerDay, CancellationToken cancellationToken)
    {
        // Keyed to the same weekday-only trading-day definition TradingDayCalendar already
        // establishes elsewhere (plateau evaluation) - not a second, competing "what day is
        // it" concept. A non-weekday evaluation is unusual (the dedicated Market-Hours gate
        // downstream is what actually rejects a non-trading-day fire attempt) but not treated
        // as an error here - just logged, then keyed by calendar date as normal.
        var tradingDate = DateOnly.FromDateTime(asOf.Date);

        if (!TradingDayCalendar.IsWeekday(asOf))
        {
            _logger.LogWarning(
                "[KILL-SWITCH]: evaluated on a non-trading day ({TradingDate}) - unusual; proceeding with the " +
                "same date-keyed counter regardless (the Market-Hours gate is the dedicated check for this)",
                tradingDate);
        }

        var counter = await _dbContext.KillSwitchCounters
            .SingleOrDefaultAsync(k => k.TradingDate == tradingDate, cancellationToken);

        if (counter is null)
        {
            counter = new KillSwitchCounter { TradingDate = tradingDate, OpenAttemptCount = 0 };
            _dbContext.KillSwitchCounters.Add(counter);
        }

        var allowed = counter.OpenAttemptCount < maxOpensPerDay;

        if (allowed)
        {
            counter.OpenAttemptCount++;
        }
        else
        {
            _logger.LogWarning(
                "[KILL-SWITCH]: tripped for {TradingDate} - {Count}/{Max} order-open attempts already recorded",
                tradingDate, counter.OpenAttemptCount, maxOpensPerDay);
        }

        counter.UpdatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return allowed;
    }

    // Returns true the first time this trading day's trip is marked notified (the caller should
    // actually send the Pushover alert), false on every subsequent call for the same
    // TradingDate (already notified - skip the SendAsync call, but the caller's own Fail()/
    // rejection result is unaffected either way). Deliberately a separate method from
    // TryRecordOpenAttemptAsync rather than folding this into that method's own return value -
    // PreFireGateService only needs to consult this once it already knows the switch is
    // tripped, and every existing TryRecordOpenAttemptAsync caller/test is unaffected by adding
    // a second, independent method here.
    public async Task<bool> TryMarkNotifiedAsync(DateTime asOf, CancellationToken cancellationToken)
    {
        var tradingDate = DateOnly.FromDateTime(asOf.Date);

        var counter = await _dbContext.KillSwitchCounters
            .SingleOrDefaultAsync(k => k.TradingDate == tradingDate, cancellationToken);

        // Should always exist by the time this is called - TryRecordOpenAttemptAsync creates
        // the row on the very same call that trips the switch, and PreFireGateService only
        // calls this method after that call already returned false. Fail safe rather than
        // throw if it's somehow missing: treat it as not-yet-notified so a real trip still
        // alerts, rather than silently swallowing the notification over a data inconsistency.
        if (counter is null)
        {
            _logger.LogWarning(
                "[KILL-SWITCH]: TryMarkNotifiedAsync found no KillSwitchCounters row for {TradingDate} - " +
                "unexpected (TryRecordOpenAttemptAsync should have created it), proceeding as not-yet-notified",
                tradingDate);
            return true;
        }

        if (counter.NotifiedAt is not null)
        {
            return false;
        }

        counter.NotifiedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
