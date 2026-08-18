using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class FieldVerificationConfiguration : IEntityTypeConfiguration<FieldVerification>
{
    public void Configure(EntityTypeBuilder<FieldVerification> builder)
    {
        builder.Property(f => f.Verified)
            .HasDefaultValue(false);

        builder.Property(f => f.ToleranceApplied).HasColumnType("decimal(9,6)");
    }
}
