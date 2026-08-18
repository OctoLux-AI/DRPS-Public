using Drps.Shared.Models;

namespace Drps.Monolith;

/// <summary>
/// One Gate lifecycle stamp (LowGradeDate/PlateauDate/ReactivatedDate/DeactivatedDate) matched
/// against the GateScore row that most plausibly produced it, by (Ticker, nearest ScanDate) -
/// there is no FK from Position to these later re-evaluation scans, only the FK to the
/// *originating* GateScore (Position.GateScoreId). MatchedScan is null when no GateScore row
/// for this ticker falls within ReplayJoinService's match tolerance of the stamp - a real,
/// honest "we can't reconstruct which scan produced this" outcome, not an error.
/// </summary>
public sealed record ReassessmentEvent(string StampName, DateTime StampTimestamp, GateScore? MatchedScan);

/// <summary>
/// One Position's full replay lineage: the GateScore that originated it (via the hard
/// Position.GateScoreId FK), plus every lifecycle stamp reconstructed against the GateScore
/// stream by timestamp. ReassessmentHistory is empty - never null, never throws - for a
/// Position that hasn't hit a single Gate re-evaluation yet (every lifecycle stamp field null),
/// the current real-world state of every open Position in production today.
/// </summary>
public sealed record PositionReplay(
    Position Position,
    GateScore? OriginatingScore,
    IReadOnlyList<ReassessmentEvent> ReassessmentHistory);

/// <summary>
/// Drps.Monolith's core replay mechanism - joins Gate's decision stream (GateScore) against
/// Ledger's outcome stream (Position) by ticker and timestamp, per CLAUDE.md's Gate/Ledger/
/// Monolith role clarification ("timestamp leverage is the correct description of Monolith's
/// actual mechanism"). Two distinct join shapes, deliberately not conflated:
///
///   1. Originating recommendation - Position.GateScoreId is a real, validated FK
///      (ManualPositionEntryService.OpenPositionAsync checks it exists before insert), so this
///      link is resolved directly by Id match, not by timestamp proximity - there is no
///      ambiguity here to reconstruct.
///   2. Later reassessments (LowGradeDate/PlateauDate/ReactivatedDate/DeactivatedDate) carry no
///      FK back to the specific later GateScore scan that produced them (the 2026-07-18
///      lifecycle-stamp-carve-out decision never added one) - these are reconstructed by
///      nearest-ScanDate match within a tolerance window, the genuine timestamp-join case this
///      class exists for.
///
/// Pure, synchronous, DB-free - same shape precedent as GateQualityScorer/GateCompositeService/
/// GatePlateauEvaluator in Drps.Gate - so this is testable against constructed fixtures without
/// a database, and later replayable against a historical "as of" date without rewriting it.
/// </summary>
public static class ReplayJoinService
{
    // Gate's LedgerLifecycleStampService stamps lifecycle fields with the same `asOf` its
    // triggering scan used for that cycle's GateScore.ScanDate rows, so an exact same-day match
    // is the common case - this tolerance exists only to absorb minor clock/precision drift
    // between the two writes, not to paper over a genuinely ambiguous match. Placeholder, same
    // "needs real data to calibrate" category as every other numeric tolerance in this codebase.
    private static readonly TimeSpan ReassessmentMatchTolerance = TimeSpan.FromHours(12);

    public static PositionReplay Join(Position position, IReadOnlyList<GateScore> gateScoresForTicker)
    {
        var originatingScore = gateScoresForTicker.FirstOrDefault(g => g.Id == position.GateScoreId);

        var reassessments = new List<ReassessmentEvent>();
        AddReassessmentIfPresent(reassessments, "LowGradeDate", position.LowGradeDate, gateScoresForTicker);
        AddReassessmentIfPresent(reassessments, "PlateauDate", position.PlateauDate, gateScoresForTicker);
        AddReassessmentIfPresent(reassessments, "ReactivatedDate", position.ReactivatedDate, gateScoresForTicker);
        AddReassessmentIfPresent(reassessments, "DeactivatedDate", position.DeactivatedDate, gateScoresForTicker);

        return new PositionReplay(position, originatingScore, reassessments);
    }

    private static void AddReassessmentIfPresent(
        List<ReassessmentEvent> events,
        string stampName,
        DateTime? stampValue,
        IReadOnlyList<GateScore> candidates)
    {
        // No stamp at all is the expected, common case for a freshly-opened position that
        // hasn't hit a Gate re-evaluation yet - simply contributes nothing to the history,
        // never a null-reference risk or an exception.
        if (stampValue is null)
        {
            return;
        }

        var matched = candidates
            .Where(g => AbsoluteDelta(g.ScanDate, stampValue.Value) <= ReassessmentMatchTolerance)
            .OrderBy(g => AbsoluteDelta(g.ScanDate, stampValue.Value))
            .FirstOrDefault();

        events.Add(new ReassessmentEvent(stampName, stampValue.Value, matched));
    }

    private static TimeSpan AbsoluteDelta(DateTime a, DateTime b) => (a - b).Duration();
}
