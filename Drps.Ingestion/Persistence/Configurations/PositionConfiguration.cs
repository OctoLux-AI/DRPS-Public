using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class PositionConfiguration : IEntityTypeConfiguration<Position>
{
    public void Configure(EntityTypeBuilder<Position> builder)
    {
        builder.Property(p => p.EntryPrice).HasColumnType("decimal(18,4)");
        builder.Property(p => p.EntryQuantity).HasColumnType("decimal(18,9)");
        builder.Property(p => p.ExitPrice).HasColumnType("decimal(18,4)");
        builder.Property(p => p.ExitQuantity).HasColumnType("decimal(18,9)");
        builder.Property(p => p.PlateauBaselinePrice).HasColumnType("decimal(18,4)");
        builder.Property(p => p.EntryAtr).HasColumnType("decimal(18,4)");
        builder.Property(p => p.TpTargetPrice).HasColumnType("decimal(18,4)");
        builder.Property(p => p.HighWaterMark).HasColumnType("decimal(18,4)");
        builder.Property(p => p.TpProgressAtExit).HasColumnType("decimal(18,4)");

        // String-backed, not the numeric default - same enum-pinning precedent as
        // GateScore.Bucket and RawSectorObservation.Source, avoids a persisted value silently
        // renumbering if PositionExitReason's members are ever reordered.
        builder.Property(p => p.ExitReason).HasConversion<string>();

        // CLAUDE.md's Execution Layer: Tenth Design Decision - same string-backed convention as
        // ExitReason above. Deliberately NO HasDefaultValue() here for OpenOrigin even though
        // it's a NOT NULL column being added to a table with existing rows - a column-level
        // default would apply to every future insert too, and every current write path already
        // supplies an explicit origin via a required constructor parameter (no code path relies
        // on or should ever rely on a silent default). The one-time backfill of existing rows to
        // Unknown belongs in the migration's own data-backfill step, not in this Fluent config -
        // see the Tenth Design Decision block's backfill note.
        builder.Property(p => p.OpenOrigin).HasConversion<string>();
        builder.Property(p => p.CloseOrigin).HasConversion<string>();

        // No ShareCapDeficient column here (removed 2026-07-18, migration
        // DropPositionShareCapDeficient) - consolidated onto AdjusterAllocation.
        // ShareCapDeficient, the single real, wired value, read through Position's existing
        // AdjusterAllocationId FK (LedgerPositionStateProvider.GetShareCapDeficient) rather
        // than duplicated as a second, independently-written column.

        // FK references only, no navigation properties either side - same pattern as
        // GateScore's FK to GateParameters and AdjusterAllocation's FK to GateScore. Restrict
        // (not the EF default Cascade): these are append-only tables, nothing should ever
        // delete a GateScore/AdjusterAllocation row out from under an existing Position, and
        // if one somehow were, it must not silently take Position rows with it.
        builder.HasOne<GateScore>()
            .WithMany()
            .HasForeignKey(p => p.GateScoreId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AdjusterAllocation>()
            .WithMany()
            .HasForeignKey(p => p.AdjusterAllocationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Concurrency token (SQL Server ROWVERSION) - IsRowVersion() sets store-generated
        // on add/update plus concurrency-check semantics in one call. Catches two writers
        // racing to close (or otherwise update) the same Position row: whichever
        // SaveChangesAsync commits first wins, the other gets a DbUpdateConcurrencyException
        // (translated to PositionAlreadyModifiedException by LedgerPositionWriter).
        builder.Property(p => p.RowVersion).IsRowVersion();

        // Filtered unique index - belt-and-suspenders duplicate-open guard. OpenCandidateQuery's
        // own idempotency check (no open Position for a ticker) already prevents this in the
        // normal path; this is the database itself refusing even if that upstream check is ever
        // bypassed or buggy. Filtered (WHERE ExitDate IS NULL) rather than a plain unique
        // constraint on Ticker alone - a ticker can and does accumulate many closed positions
        // over time (append-only), only ever one open at a time.
        builder.HasIndex(p => p.Ticker)
            .IsUnique()
            .HasFilter("[ExitDate] IS NULL");
    }
}
