using Drps.Shared.Models;

namespace Drps.Monolith;

/// <summary>
/// Ties ReplayJoinService and GradingService together across a whole Position set - one grade
/// per Position, each joined only against GateScore rows sharing that Position's own ticker
/// (Monolith's replay is always scoped per-ticker; cross-ticker matches would be meaningless).
/// Does not touch the database itself - callers (Program.cs for a live run, or a test fixture)
/// supply already-loaded, already-synthetic-filtered collections.
/// </summary>
public static class GradingReportBuilder
{
    public static IReadOnlyList<PositionGrade> BuildReport(
        IReadOnlyList<GateScore> gateScores, IReadOnlyList<Position> positions)
    {
        var grades = new List<PositionGrade>();

        foreach (var position in positions)
        {
            var scoresForTicker = gateScores.Where(g => g.Ticker == position.Ticker).ToList();
            var replay = ReplayJoinService.Join(position, scoresForTicker);
            grades.Add(GradingService.Grade(replay));
        }

        return grades;
    }
}
