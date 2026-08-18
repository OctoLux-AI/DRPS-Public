using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Calculator.Persistence.Configurations;

/// <summary>
/// Drps.Calculator only reads RawOhlcvBar rows - the table itself is owned and migrated by
/// Drps.Ingestion. ExcludeFromMigrations keeps CalculatorDbContext's own migrations from
/// trying to (re)create a table that already exists in the shared database.
/// </summary>
public class RawOhlcvBarReadOnlyConfiguration : IEntityTypeConfiguration<RawOhlcvBar>
{
    public void Configure(EntityTypeBuilder<RawOhlcvBar> builder)
    {
        builder.ToTable("RawOhlcvBars", t => t.ExcludeFromMigrations());

        builder.Property(b => b.Open).HasColumnType("decimal(18,4)");
        builder.Property(b => b.High).HasColumnType("decimal(18,4)");
        builder.Property(b => b.Low).HasColumnType("decimal(18,4)");
        builder.Property(b => b.Close).HasColumnType("decimal(18,4)");
    }
}
