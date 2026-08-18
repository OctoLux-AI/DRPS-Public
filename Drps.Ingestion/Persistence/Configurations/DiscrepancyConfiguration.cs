using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class DiscrepancyConfiguration : IEntityTypeConfiguration<Discrepancy>
{
    public void Configure(EntityTypeBuilder<Discrepancy> builder)
    {
        builder.Property(d => d.ValueA).HasColumnType("decimal(18,9)");
        builder.Property(d => d.ValueB).HasColumnType("decimal(18,9)");
        builder.Property(d => d.PercentDiff).HasColumnType("decimal(9,6)");

        // String-backed, not the numeric default - same enum-pinning precedent as
        // GateScore.Bucket/Position.ExitReason, avoids a persisted value silently
        // renumbering if DiscrepancyResolutionMethod's members are ever reordered.
        // Defaults to Unresolved so every discrepancy logged before this column existed
        // (and any row the new OHL-agreed exception doesn't apply to) reads as the
        // ordinary, no-preference-applied case.
        builder.Property(d => d.ResolutionMethod)
            .HasConversion<string>()
            .HasDefaultValue(DiscrepancyResolutionMethod.Unresolved);
    }
}
