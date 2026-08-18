using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Calculator.Persistence.Configurations;

public class DmaIndicatorConfiguration : IEntityTypeConfiguration<DmaIndicator>
{
    public void Configure(EntityTypeBuilder<DmaIndicator> builder)
    {
        builder.Property(d => d.Value).HasColumnType("decimal(18,4)");
        builder.Property(d => d.HasExDividendEvent).HasDefaultValue(false);
        builder.Property(d => d.HasTiingoCorrectedClose).HasDefaultValue(false);

        // String-backed, not the numeric default - same enum-pinning precedent as
        // Position.OpenOrigin/CloseOrigin, avoids a persisted value silently renumbering if
        // TickerSourceOrigin's members are ever reordered. Deliberately NO HasDefaultValue()
        // here, same reasoning as Position.OpenOrigin: every current write path
        // (DmaComputationService.ComputeAsync's required parameter) already supplies an
        // explicit origin - the one-time backfill of rows that predate this column belongs in
        // the migration's own data-backfill step, not in this Fluent config.
        builder.Property(d => d.TickerSourceOrigin).HasConversion<string>();

        // Owned/migrated by Drps.Calculator - the one new table this task adds. Guards
        // against the same (Symbol, BarDate, Window, CalculationVersion) row being inserted
        // twice across separate scheduled runs.
        builder.HasIndex(d => new { d.Symbol, d.BarDate, d.Window, d.CalculationVersion })
            .IsUnique();
    }
}
