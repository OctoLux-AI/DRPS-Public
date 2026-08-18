using Drps.Shared.Models;

namespace Drps.Monolith;

public enum PositionOutcome
{
    StillOpen,
    Win,
    Loss,
    Breakeven
}

/// <summary>
/// One Position's grading summary - the minimum this task's scope asked for: was the
/// originating Gate recommendation acted on, how long between recommendation and human action,
/// and the real outcome where ExitPrice exists. Deliberately not a sophisticated metric set
/// yet - Drps.Monolith has no real, non-synthetic Position data to calibrate anything more
/// elaborate against today (see CLAUDE.md's "Ledger Live Verification Pass" block).
/// </summary>
public sealed record PositionGrade(
    long PositionId,
    string Ticker,
    bool WasRecommendationActedOn,
    TimeSpan? TimeFromRecommendationToAction,
    PositionOutcome Outcome,
    decimal? RealizedPnLPerShare,
    string ReassessmentSummary);

/// <summary>
/// Turns a single PositionReplay (ReplayJoinService's join output) into a readable
/// PositionGrade. Pure, synchronous, DB-free - same testability shape as ReplayJoinService.
/// </summary>
public static class GradingService
{
    public static PositionGrade Grade(PositionReplay replay)
    {
        var position = replay.Position;
        var actedOn = replay.OriginatingScore is not null;

        // Only meaningful when the originating recommendation actually resolved - a Position
        // whose GateScoreId doesn't match any supplied GateScore (e.g. a legacy row, or one
        // scoped outside the caller's query window) has nothing to measure a gap against.
        TimeSpan? timeToAction = actedOn
            ? position.EntryDate - replay.OriginatingScore!.ScanDate
            : null;

        var outcome = DetermineOutcome(position);

        decimal? realizedPnLPerShare = position.ExitPrice.HasValue
            ? position.ExitPrice.Value - position.EntryPrice
            : null;

        var reassessmentSummary = replay.ReassessmentHistory.Count == 0
            ? "no reassessment history"
            : string.Join("; ", replay.ReassessmentHistory.Select(DescribeReassessment));

        return new PositionGrade(
            position.Id,
            position.Ticker,
            actedOn,
            timeToAction,
            outcome,
            realizedPnLPerShare,
            reassessmentSummary);
    }

    private static PositionOutcome DetermineOutcome(Position position)
    {
        // ExitDate/ExitPrice are only ever set together, by ManualPositionEntryService.
        // ClosePositionAsync - checking ExitPrice alone would work, but checking both makes the
        // "still open" branch's intent explicit rather than incidental.
        if (position.ExitDate is null || position.ExitPrice is null)
        {
            return PositionOutcome.StillOpen;
        }

        if (position.ExitPrice.Value > position.EntryPrice)
        {
            return PositionOutcome.Win;
        }

        if (position.ExitPrice.Value < position.EntryPrice)
        {
            return PositionOutcome.Loss;
        }

        return PositionOutcome.Breakeven;
    }

    private static string DescribeReassessment(ReassessmentEvent reassessment) =>
        reassessment.MatchedScan is not null
            ? $"{reassessment.StampName} @ {reassessment.StampTimestamp:yyyy-MM-dd} matched GateScore {reassessment.MatchedScan.Id} (scanned {reassessment.MatchedScan.ScanDate:yyyy-MM-dd})"
            : $"{reassessment.StampName} @ {reassessment.StampTimestamp:yyyy-MM-dd} (no matching GateScore found)";
}
