using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class RawEarningsObservationConfiguration : IEntityTypeConfiguration<RawEarningsObservation>
{
    public void Configure(EntityTypeBuilder<RawEarningsObservation> builder)
    {
        // Fail-closed default (Unknown), same precedent as BarVerification/FieldVerification/
        // RawSectorObservation's Verified column previously enforced. String-backed, not the
        // numeric default - same reasoning as Bucket/PositionExitReason/PositionActionOrigin
        // elsewhere in this codebase: a raw int would silently renumber if
        // EarningsFetchOutcome's members were ever reordered.
        builder.Property(o => o.FetchOutcome)
            .HasConversion<string>()
            .HasDefaultValue(EarningsFetchOutcome.Unknown);
    }
}
