using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class RawRegimeObservationConfiguration : IEntityTypeConfiguration<RawRegimeObservation>
{
    public void Configure(EntityTypeBuilder<RawRegimeObservation> builder)
    {
        builder.Property(o => o.Open).HasColumnType("decimal(18,4)");
        builder.Property(o => o.High).HasColumnType("decimal(18,4)");
        builder.Property(o => o.Low).HasColumnType("decimal(18,4)");
        builder.Property(o => o.Close).HasColumnType("decimal(18,4)");

        // String-backed, not the numeric default - same enum-pinning precedent as
        // GateScore.Bucket/RawSectorObservation.Source, avoids a persisted value silently
        // renumbering if RegimeSourceType's members are ever reordered.
        builder.Property(o => o.Source).HasConversion<string>();
    }
}
