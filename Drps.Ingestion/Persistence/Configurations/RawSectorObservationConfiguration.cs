using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class RawSectorObservationConfiguration : IEntityTypeConfiguration<RawSectorObservation>
{
    public void Configure(EntityTypeBuilder<RawSectorObservation> builder)
    {
        // Fail-closed default: an inserted row makes no verification claim unless the
        // application explicitly asserts it (Finnhub rows only, per this entity's own
        // n=1-by-design rule) - mirrors BarVerification/FieldVerification's existing
        // HasDefaultValue(false) precedent.
        builder.Property(o => o.Verified).HasDefaultValue(false);

        // String-backed, not the numeric default - same enum-pinning precedent as
        // GateScore.Bucket, avoids a persisted value silently renumbering if
        // SectorSourceType's members are ever reordered.
        builder.Property(o => o.Source).HasConversion<string>();
    }
}
