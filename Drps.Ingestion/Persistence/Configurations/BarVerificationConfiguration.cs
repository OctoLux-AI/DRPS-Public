using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class BarVerificationConfiguration : IEntityTypeConfiguration<BarVerification>
{
    public void Configure(EntityTypeBuilder<BarVerification> builder)
    {
        builder.Property(b => b.Verified)
            .HasDefaultValue(false);

        builder.Property(b => b.ToleranceApplied).HasColumnType("decimal(9,6)");
        builder.Property(b => b.PrimarySourceValue).HasColumnType("decimal(18,4)");
    }
}
