using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class PositionReconciliationDiscrepancyConfiguration : IEntityTypeConfiguration<PositionReconciliationDiscrepancy>
{
    public void Configure(EntityTypeBuilder<PositionReconciliationDiscrepancy> builder)
    {
        builder.Property(d => d.LedgerQuantity).HasColumnType("decimal(18,9)");
        builder.Property(d => d.AlpacaQuantity).HasColumnType("decimal(18,9)");
        builder.Property(d => d.LedgerPrice).HasColumnType("decimal(18,4)");
        builder.Property(d => d.AlpacaAverageEntryPrice).HasColumnType("decimal(18,4)");

        // FK reference only, no navigation property either side - same pattern as GateScore's
        // FK to GateParameters and Position's own FKs to GateScore/AdjusterAllocation.
        // Restrict (not the EF default Cascade): a Position must never be deletable out from
        // under a logged discrepancy referencing it, and if one somehow were, it must not
        // silently take discrepancy rows with it.
        builder.HasOne<Position>()
            .WithMany()
            .HasForeignKey(d => d.PositionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
