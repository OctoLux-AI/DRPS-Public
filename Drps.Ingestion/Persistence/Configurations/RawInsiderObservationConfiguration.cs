using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class RawInsiderObservationConfiguration : IEntityTypeConfiguration<RawInsiderObservation>
{
    public void Configure(EntityTypeBuilder<RawInsiderObservation> builder)
    {
        builder.Property(o => o.DollarValue).HasColumnType("decimal(18,4)");

        // Fail-closed default, same precedent as BarVerification/FieldVerification/
        // RawSectorObservation/RawEarningsObservation's Verified column.
        builder.Property(o => o.Verified).HasDefaultValue(false);
    }
}
