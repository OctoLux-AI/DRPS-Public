using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class AdjusterAllocationConfiguration : IEntityTypeConfiguration<AdjusterAllocation>
{
    public void Configure(EntityTypeBuilder<AdjusterAllocation> builder)
    {
        builder.Property(a => a.AllocationPercent).HasColumnType("decimal(18,4)");
        builder.Property(a => a.AllocationDollarAmount).HasColumnType("decimal(18,4)");
        // Matches Position.EntryQuantity/ExitQuantity's fractional-share precision.
        builder.Property(a => a.ShareCount).HasColumnType("decimal(18,9)");
        builder.Property(a => a.InsiderMultiplierApplied).HasColumnType("decimal(18,4)").HasDefaultValue(1.0m);

        // Fail-closed default, same precedent as GateScore's HasUnverifiedPartialCreditData/
        // IsStaleData columns.
        builder.Property(a => a.InsiderDataUnverified).HasDefaultValue(false);

        // Same fail-closed-to-neutral precedent as InsiderMultiplierApplied above - a DB-level
        // default of 1.0m (not 0m) reinforces the contract at the schema level too, in case any
        // future insert path other than AdjusterScanService ever forgets to set this explicitly.
        builder.Property(a => a.OptionsFlowMultiplierApplied).HasColumnType("decimal(18,4)").HasDefaultValue(1.0m);

        // FK reference to GateScore.Id, no navigation property either side - same pattern as
        // GateScore's own FK to GateParameters. Restrict (not the EF default Cascade): these
        // are append-only tables, nothing should ever delete a GateScore row out from under
        // an existing AdjusterAllocation, and if one somehow were, it must not silently take
        // AdjusterAllocation rows with it.
        builder.HasOne<GateScore>()
            .WithMany()
            .HasForeignKey(a => a.GateScoreId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
