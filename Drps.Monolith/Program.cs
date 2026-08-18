using Drps.Ingestion.Persistence;
using Drps.Monolith;
using Microsoft.EntityFrameworkCore;

// Human-operated CLI, not a hosted service - Drps.Monolith has no scheduled loop of its own
// (it's a retrospective replay/grading tool, run on demand). Same shape precedent as
// Drps.Ledger's Program.cs: skips the Generic Host / config-binding ceremony, connects
// directly to the same default LocalDB connection string every other project falls back to.
var options = new DbContextOptionsBuilder<DrpsDbContext>()
    .UseSqlServer(@"Server=(localdb)\mssqllocaldb;Database=Drps;Trusted_Connection=True;")
    .Options;

using var dbContext = new DrpsDbContext(options);
var loader = new MonolithDataLoader(dbContext);

var gateScores = await loader.LoadRealGateScoresAsync(CancellationToken.None);
var positions = await loader.LoadRealPositionsAsync(CancellationToken.None);

Console.WriteLine(
    $"Loaded {gateScores.Count} real (non-synthetic) GateScore row(s) and {positions.Count} real (non-synthetic) Position row(s).");

if (positions.Count == 0)
{
    // Honest, expected state as of this task: the only Position rows in production today are
    // the hand-seeded synthetic ones (Ids 1-2, per CLAUDE.md's "Ledger Live Verification Pass"
    // block), now excluded above. There is nothing real to grade yet. This is NOT a bug in the
    // loader/join/grading logic - do not manufacture fake rows to make this print something.
    Console.WriteLine("No real (non-synthetic) Position rows exist yet - nothing to grade. This is expected, not an error.");
    return 0;
}

var grades = GradingReportBuilder.BuildReport(gateScores, positions);

foreach (var grade in grades)
{
    Console.WriteLine(
        $"Position {grade.PositionId} ({grade.Ticker}): actedOn={grade.WasRecommendationActedOn}, " +
        $"timeToAction={grade.TimeFromRecommendationToAction?.ToString() ?? "n/a"}, " +
        $"outcome={grade.Outcome}, pnlPerShare={grade.RealizedPnLPerShare?.ToString("F4") ?? "n/a"}, " +
        $"reassessments=[{grade.ReassessmentSummary}]");
}

return 0;
