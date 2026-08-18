using Drps.Ingestion.Persistence;
using Drps.Shared.Models;

namespace Drps.Ledger;

/// <summary>
/// Drps.Ledger's manual entry point - real trades, logged by a human, matching
/// GateParametersSeeder's precedent (deliberate, human-triggered data entry, not automation).
/// Per Ledger: First Design Decision, an automated execution layer is explicitly deferred;
/// this is how Position rows come into existence today.
///
/// Thin wrapper over LedgerPositionWriter (CLAUDE.md's Execution Layer: Fourth Design
/// Decision's shared, not duplicated, write path) - this class carries no field-setting or
/// validation logic of its own anymore. Same public API, same behavior as before this
/// refactor: Drps.Ledger/Program.cs's CLI and every existing ManualPositionEntryServiceTests
/// test call these two methods exactly as they did pre-refactor.
///
/// Per Ledger: Fourth Design Decision's Gate/Adjuster write carve-out, ExitDate/ExitPrice/
/// ExitQuantity/ExitReason are set ONLY by ClosePositionAsync below - Gate/Adjuster may stamp
/// LowGradeDate/PlateauDate/ReactivatedDate/DeactivatedDate/CoolDownStartDate/
/// PlateauBaselinePrice/PlateauBaselineDate on an existing row (via LedgerLifecycleStampService,
/// not this class), but must never touch these four Exit* fields. ShareCapDeficient is no
/// longer a Position column at all - consolidated onto AdjusterAllocation.ShareCapDeficient,
/// see Position.cs's own doc comment on AdjusterAllocationId.
/// </summary>
public class ManualPositionEntryService
{
    private readonly LedgerPositionWriter _writer;

    public ManualPositionEntryService(DrpsDbContext dbContext)
    {
        _writer = new LedgerPositionWriter(dbContext);
    }

    public Task<Position> OpenPositionAsync(
        long gateScoreId,
        long adjusterAllocationId,
        string ticker,
        DateTime entryDate,
        decimal entryPrice,
        decimal entryQuantity,
        CancellationToken cancellationToken) =>
        // PositionActionOrigin.Manual is hardcoded, not exposed as a parameter - CLAUDE.md's
        // Execution Layer: Tenth Design Decision. This class's entire reason to exist is being
        // the manual path (Ledger: First Design Decision), so it is the only origin value it
        // will ever pass - Kent should never have to remember to pass it correctly from the CLI.
        _writer.OpenPositionAsync(gateScoreId, adjusterAllocationId, ticker, entryDate, entryPrice, entryQuantity, cancellationToken, PositionActionOrigin.Manual);

    public Task ClosePositionAsync(
        long positionId,
        DateTime exitDate,
        decimal exitPrice,
        decimal exitQuantity,
        PositionExitReason exitReason,
        CancellationToken cancellationToken) =>
        // PositionActionOrigin.Manual is hardcoded, same reasoning as OpenPositionAsync above.
        _writer.ClosePositionAsync(positionId, exitDate, exitPrice, exitQuantity, exitReason, cancellationToken, PositionActionOrigin.Manual);
}
