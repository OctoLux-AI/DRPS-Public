using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class GateParametersConfiguration : IEntityTypeConfiguration<GateParameters>
{
    public void Configure(EntityTypeBuilder<GateParameters> builder)
    {
        builder.Property(p => p.RsiLowerBound).HasColumnType("decimal(18,4)");
        builder.Property(p => p.RsiPeak).HasColumnType("decimal(18,4)");
        builder.Property(p => p.RsiUpperBound).HasColumnType("decimal(18,4)");
        builder.Property(p => p.RsiFloorQuality).HasColumnType("decimal(18,4)");
        builder.Property(p => p.RvolFloorMultiple).HasColumnType("decimal(18,4)");
        builder.Property(p => p.RvolCeilingMultiple).HasColumnType("decimal(18,4)");
        builder.Property(p => p.RvolFullWeight).HasColumnType("decimal(18,4)");
        builder.Property(p => p.RvolHalfWeight).HasColumnType("decimal(18,4)");
        builder.Property(p => p.RsiCompositeWeight).HasColumnType("decimal(18,4)");
        builder.Property(p => p.BuyThreshold).HasColumnType("decimal(18,4)");
        builder.Property(p => p.WatchThreshold).HasColumnType("decimal(18,4)");
        builder.Property(p => p.ExitThreshold).HasColumnType("decimal(18,4)");

        // Fail-closed: a row is not live until explicitly activated, same precedent as
        // BarVerification/FieldVerification/RawExDividendObservation's Verified default.
        builder.Property(p => p.IsActive).HasDefaultValue(false);
    }
}
