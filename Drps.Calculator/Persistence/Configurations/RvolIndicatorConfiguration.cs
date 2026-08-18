using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Calculator.Persistence.Configurations;

public class RvolIndicatorConfiguration : IEntityTypeConfiguration<RvolIndicator>
{
    public void Configure(EntityTypeBuilder<RvolIndicator> builder)
    {
        builder.Property(r => r.Value).HasColumnType("decimal(18,4)");
        builder.Property(r => r.HasExDividendEvent).HasDefaultValue(false);
        builder.Property(r => r.HasTiingoCorrectedClose).HasDefaultValue(false);

        // String-backed, not the numeric default - same enum-pinning precedent as
        // Position.OpenOrigin/CloseOrigin and DmaIndicatorConfiguration.TickerSourceOrigin,
        // avoids a persisted value silently renumbering if TickerSourceOrigin's members are
        // ever reordered. Deliberately NO HasDefaultValue() here, same reasoning as
        // DmaIndicatorConfiguration: every current write path (RvolComputationService.
        // ComputeAsync's required parameter) already supplies an explicit origin - the
        // one-time backfill of rows that predate this column belongs in the migration's own
        // data-backfill step, not in this Fluent config.
        builder.Property(r => r.TickerSourceOrigin).HasConversion<string>();

        // Owned/migrated by Drps.Calculator. Guards against the same (Symbol, BarDate,
        // BaselineWindow, CalculationVersion) row being inserted twice across separate
        // scheduled runs.
        builder.HasIndex(r => new { r.Symbol, r.BarDate, r.BaselineWindow, r.CalculationVersion })
            .IsUnique();
    }
}
