using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Calculator.Persistence.Configurations;

/// <summary>
/// Drps.Calculator only reads Discrepancy rows (to find which dates were resolved via the
/// OHL-agreed/Close-resolved-to-Tiingo exception) - the table itself is owned and migrated by
/// Drps.Ingestion. See RawOhlcvBarReadOnlyConfiguration/BarVerificationReadOnlyConfiguration
/// for why ExcludeFromMigrations is required here too. Property mappings mirror
/// DiscrepancyConfiguration (Drps.Ingestion) exactly - in particular, ResolutionMethod's
/// string conversion must match or Drps.Calculator would read the column as the wrong type.
/// </summary>
public class DiscrepancyReadOnlyConfiguration : IEntityTypeConfiguration<Discrepancy>
{
    public void Configure(EntityTypeBuilder<Discrepancy> builder)
    {
        builder.ToTable("Discrepancies", t => t.ExcludeFromMigrations());

        builder.Property(d => d.ValueA).HasColumnType("decimal(18,9)");
        builder.Property(d => d.ValueB).HasColumnType("decimal(18,9)");
        builder.Property(d => d.PercentDiff).HasColumnType("decimal(9,6)");
        builder.Property(d => d.ResolutionMethod).HasConversion<string>();
    }
}
