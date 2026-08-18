using System.Collections.Concurrent;

namespace Drps.Execution;

/// <summary>
/// Process-lifetime in-flight ticker set (CLAUDE.md's Execution Layer: Sixth Design Decision),
/// extracted from OrchestrationWorker so the not-yet-built ATR ratchet monitor worker can share
/// the exact same in-flight state rather than each worker racing to close the same position
/// independently with its own separate set. Registered as a singleton - both workers must see
/// the SAME instance.
///
/// Also owns the concurrent-position-cap reservation (CLAUDE.md's "Adjuster: Concurrent-Position
/// Cap Introduced" decision, and the same-day follow-up closing the race this class's own doc
/// comment describes below) - a second, distinct piece of shared state, not a repurposing of the
/// per-ticker in-flight set above.
/// </summary>
public interface IInFlightPositionTracker
{
    /// <summary>
    /// Marks a ticker in-flight. Returns true if it was not already in-flight (caller should
    /// proceed); false if it was already in-flight (caller should skip).
    /// </summary>
    bool TryMarkInFlight(string ticker);

    /// <summary>
    /// Releases a ticker's in-flight marker, however its processing resolved. Also releases that
    /// ticker's concurrent-position-cap reservation, if one was made via TryReserveOpenSlot below
    /// - a single release point for both, since both share the same "however this candidate's
    /// processing resolved" lifecycle and the same caller (OrchestrationWorker's finally block).
    /// </summary>
    void Release(string ticker);

    /// <summary>
    /// Atomically checks and reserves one concurrent-position-cap slot for `ticker`, closing a
    /// real race PreFireGateService.EvaluateConcurrentPositionCapAsync's own DB-count check
    /// cannot close by itself: OrchestrationWorker fires multiple candidates from one cycle as
    /// independent, concurrent detached tasks (see OrchestrationWorker's own doc comment), and
    /// none of them has a real Position row yet until its own fill confirmation completes -
    /// firing 20-30+ seconds to several minutes later. Without this reservation, several
    /// concurrently-firing candidates could each read the same pre-fire open count and all pass
    /// independently, letting the cap be exceeded.
    ///
    /// `currentOpenPositionCount` is the caller's own live DB read (taken immediately before this
    /// call) - this method adds the count of currently-reserved-but-not-yet-real slots on top of
    /// it, under a single lock, so the accept/reject decision reflects every other candidate
    /// racing to reserve a slot at this same moment, not just what the database itself shows
    /// right now. Returns true (reserved) if the total is still under `maxConcurrentPositions`;
    /// false (rejected) otherwise. A caller that receives true MUST eventually call Release(
    /// ticker) - dry-run, gate rejection by a later check, terminal firing failure,
    /// fill-confirmation timeout, and an unhandled exception must all release it, the same
    /// "every outcome" requirement OrchestrationWorker's existing finally block already satisfies
    /// for the per-ticker in-flight marker above.
    ///
    /// Known, accepted limitation: this closes the in-process, same-cycle race described above,
    /// not every possible staleness window. `currentOpenPositionCount` can still be a few moments
    /// stale relative to a Position created by a completely separate process/restart between the
    /// caller's read and this call - closing that would need a database-level atomic check
    /// (a serializable transaction or an equivalent server-side constraint), a larger change than
    /// this in-process fix. Never lets the cap be exceeded by the race this was built for; can in
    /// principle be slightly more conservative than a perfectly fresh count would require, which
    /// is the correct direction to err in given this codebase's fail-closed conventions.
    /// </summary>
    bool TryReserveOpenSlot(string ticker, int currentOpenPositionCount, int maxConcurrentPositions);
}

public sealed class InFlightPositionTracker : IInFlightPositionTracker
{
    // Value is unused (a plain marker) - ConcurrentDictionary is used as the concurrent-set
    // idiom since .NET has no built-in ConcurrentHashSet.
    private readonly ConcurrentDictionary<string, byte> _inFlightTickers = new();

    // Deliberately a plain HashSet behind a lock, not a ConcurrentDictionary - the reservation
    // decision (TryReserveOpenSlot below) needs to check-then-add as one atomic step, which a
    // ConcurrentDictionary's individual thread-safe operations don't give for free without the
    // same lock this already uses.
    private readonly HashSet<string> _reservedOpenSlotTickers = new();
    private readonly object _reservationLock = new();

    public bool TryMarkInFlight(string ticker) => _inFlightTickers.TryAdd(ticker, 0);

    public void Release(string ticker)
    {
        _inFlightTickers.TryRemove(ticker, out _);

        lock (_reservationLock)
        {
            _reservedOpenSlotTickers.Remove(ticker);
        }
    }

    public bool TryReserveOpenSlot(string ticker, int currentOpenPositionCount, int maxConcurrentPositions)
    {
        lock (_reservationLock)
        {
            if (currentOpenPositionCount + _reservedOpenSlotTickers.Count >= maxConcurrentPositions)
            {
                return false;
            }

            // HashSet.Add is idempotent (returns false, changes nothing, if already present) -
            // a ticker calling this twice without an intervening Release would not double-reserve
            // a slot for itself, though nothing in this codebase's actual call pattern does that
            // (TryMarkInFlight's own per-ticker exclusivity already prevents a ticker from being
            // processed twice concurrently in the first place).
            _reservedOpenSlotTickers.Add(ticker);
            return true;
        }
    }
}
