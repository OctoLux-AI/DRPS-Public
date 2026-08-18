using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Calculator.Persistence.Configurations;

/// <summary>
/// Drps.Calculator only reads BarVerification rows (to find which raw bars are already
/// cross-source verified) - the table itself is owned and migrated by Drps.Ingestion. See
/// RawOhlcvBarReadOnlyConfiguration for why ExcludeFromMigrations is required here too.
/// </summary>
public class BarVerificationReadOnlyConfiguration : IEntityTypeConfiguration<BarVerification>
{
    public void Configure(EntityTypeBuilder<BarVerification> builder)
    {
        builder.ToTable("BarVerifications", t => t.ExcludeFromMigrations());

        builder.Property(b => b.ToleranceApplied).HasColumnType("decimal(9,6)");
        builder.Property(b => b.PrimarySourceValue).HasColumnType("decimal(18,4)");
    }
}
