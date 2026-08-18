using System.Globalization;
using Drps.Ingestion.Persistence;
using Drps.Ledger;
using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;

// Human-operated CLI, not a hosted service - Drps.Ledger has no scheduled loop of its own
// (Ledger: Sixth Design Decision). Deliberately skips the Generic Host / config-binding
// ceremony every other project's Program.cs uses; this is meant to be the cheapest possible
// way to call ManualPositionEntryService by hand. Same default LocalDB connection string
// fallback every other project uses - no override support here, since this tool is only ever
// run locally against the same database everything else already targets.
if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var options = new DbContextOptionsBuilder<DrpsDbContext>()
    .UseSqlServer(@"Server=(localdb)\mssqllocaldb;Database=Drps;Trusted_Connection=True;")
    .Options;

using var dbContext = new DrpsDbContext(options);
var service = new ManualPositionEntryService(dbContext);

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "open":
            {
                if (args.Length != 7)
                {
                    PrintUsage();
                    return 1;
                }

                var opened = await service.OpenPositionAsync(
                    ticker: args[1],
                    gateScoreId: long.Parse(args[2], CultureInfo.InvariantCulture),
                    adjusterAllocationId: long.Parse(args[3], CultureInfo.InvariantCulture),
                    entryDate: DateTime.Parse(args[4], CultureInfo.InvariantCulture),
                    entryPrice: decimal.Parse(args[5], CultureInfo.InvariantCulture),
                    entryQuantity: decimal.Parse(args[6], CultureInfo.InvariantCulture),
                    cancellationToken: CancellationToken.None);

                Console.WriteLine(
                    $"Opened Position {opened.Id}: {opened.Ticker} x{opened.EntryQuantity} @ {opened.EntryPrice} on {opened.EntryDate:d}");
                return 0;
            }

        case "reset-circuit-breaker":
            {
                // CLAUDE.md's Execution Layer: Consecutive-Loss Circuit Breaker Design Decision,
                // point 4 - the decided manual reset path. Deliberately no web API/controller
                // layer: this is the same direct-DbContext, zero-DI-host CLI shape as the
                // open/close subcommands above, not a new mechanism.
                if (args.Length != 1)
                {
                    PrintUsage();
                    return 1;
                }

                var breaker = await dbContext.ConsecutiveLossCircuitBreakers.SingleOrDefaultAsync();
                if (breaker is null)
                {
                    breaker = new ConsecutiveLossCircuitBreaker();
                    dbContext.ConsecutiveLossCircuitBreakers.Add(breaker);
                }

                // Zeroing the count (not just clearing Tripped) is deliberate - a reset is a
                // clean slate; leaving a stale count of 2 or 3 in place would let the very next
                // ordinary loss re-trip immediately.
                breaker.Tripped = false;
                breaker.ConsecutiveLossCount = 0;
                breaker.NotifiedAt = null;
                breaker.ResetAt = DateTimeOffset.UtcNow;
                breaker.UpdatedAt = DateTimeOffset.UtcNow;

                await dbContext.SaveChangesAsync();

                Console.WriteLine("Circuit breaker reset: Tripped=false, ConsecutiveLossCount=0.");
                return 0;
            }

        case "close":
            {
                if (args.Length != 6)
                {
                    PrintUsage();
                    return 1;
                }

                var positionId = long.Parse(args[1], CultureInfo.InvariantCulture);
                await service.ClosePositionAsync(
                    positionId: positionId,
                    exitDate: DateTime.Parse(args[2], CultureInfo.InvariantCulture),
                    exitPrice: decimal.Parse(args[3], CultureInfo.InvariantCulture),
                    exitQuantity: decimal.Parse(args[4], CultureInfo.InvariantCulture),
                    exitReason: Enum.Parse<PositionExitReason>(args[5], ignoreCase: true),
                    cancellationToken: CancellationToken.None);

                Console.WriteLine($"Closed Position {positionId}.");
                return 0;
            }

        default:
            PrintUsage();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""
        Drps.Ledger manual position entry tool - human-operated, not automated.

        Usage:
          dotnet run -- open  <ticker> <gateScoreId> <adjusterAllocationId> <entryDate> <entryPrice> <entryQuantity>
          dotnet run -- close <positionId> <exitDate> <exitPrice> <exitQuantity> <exitReason>
          dotnet run -- reset-circuit-breaker

        exitReason: AtrStop | CompositeDegradation | PlateauDeactivation | SectorDisplacement | PositionCountDisplacement
        Dates: any format DateTime.Parse accepts, e.g. 2026-07-16.
        reset-circuit-breaker: manually clears a tripped consecutive-loss circuit breaker
          (Tripped=false, ConsecutiveLossCount=0) - the only way to resume opens after a trip;
          there is no auto-reset.
        """);
}
