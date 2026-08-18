using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class AdjusterParametersConfiguration : IEntityTypeConfiguration<AdjusterParameters>
{
    public void Configure(EntityTypeBuilder<AdjusterParameters> builder)
    {
        builder.Property(p => p.TierOneFloor).HasColumnType("decimal(18,4)");
        builder.Property(p => p.TierOneCeiling).HasColumnType("decimal(18,4)");
        builder.Property(p => p.TierTwoCeiling).HasColumnType("decimal(18,4)");
        builder.Property(p => p.TierOneBaseRate).HasColumnType("decimal(18,4)");
        builder.Property(p => p.TierTwoBaseRate).HasColumnType("decimal(18,4)");
        builder.Property(p => p.TierThreeBaseRate).HasColumnType("decimal(18,4)");
        builder.Property(p => p.SectorCapPercent).HasColumnType("decimal(18,4)");
        builder.Property(p => p.BaseReservePercent).HasColumnType("decimal(18,4)");
        builder.Property(p => p.ReserveStepPercent).HasColumnType("decimal(18,4)");
        builder.Property(p => p.ReserveMilestoneOne).HasColumnType("decimal(18,4)");
        builder.Property(p => p.ReserveMilestoneTwo).HasColumnType("decimal(18,4)");
        builder.Property(p => p.ConcurrentPositionDisplacementMarginPercent).HasColumnType("decimal(18,4)");

        // Fail-closed: a row is not live until explicitly activated, same precedent as
        // GateParameters.IsActive.
        builder.Property(p => p.IsActive).HasDefaultValue(false);

        // Explicit DB-level default (unlike GateParameters.NoBuySessionCount, which has none) -
        // AdjusterParameters has no seeder (its one real row was inserted directly), so this
        // default is what correctly backfills that existing row to 15 rather than a silent 0
        // when this column's migration runs.
        builder.Property(p => p.MaxConcurrentPositions).HasDefaultValue(15);

        // Same backfill reasoning as MaxConcurrentPositions above - this column is also being
        // added after AdjusterParameters' one real row already exists, so a silent 0m (no
        // margin at all) would incorrectly backfill that row rather than the real 0.10
        // starting value.
        builder.Property(p => p.ConcurrentPositionDisplacementMarginPercent).HasDefaultValue(0.10m);
    }
}
