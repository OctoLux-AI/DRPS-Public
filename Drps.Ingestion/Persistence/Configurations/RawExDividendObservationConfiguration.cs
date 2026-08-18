using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class RawExDividendObservationConfiguration : IEntityTypeConfiguration<RawExDividendObservation>
{
    public void Configure(EntityTypeBuilder<RawExDividendObservation> builder)
    {
        builder.Property(o => o.Value).HasColumnType("decimal(18,4)");
        builder.Property(o => o.VariancePct).HasColumnType("decimal(18,4)");

        // Fail-closed, same as BarVerification/FieldVerification's Verified default.
        builder.Property(o => o.Verified).HasDefaultValue(false);
    }
}
