using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Calculator.Persistence.Configurations;

public class AtrIndicatorConfiguration : IEntityTypeConfiguration<AtrIndicator>
{
    public void Configure(EntityTypeBuilder<AtrIndicator> builder)
    {
        builder.Property(a => a.Value).HasColumnType("decimal(18,4)");
        builder.Property(a => a.HasExDividendEvent).HasDefaultValue(false);
        builder.Property(a => a.HasTiingoCorrectedClose).HasDefaultValue(false);

        // Always true - see AtrIndicator's own doc comment for why. Defaulted at the DB
        // level too (not just the C# property initializer) so the disclaimer holds even for
        // a row inserted via a path that doesn't go through the C# default.
        builder.Property(a => a.VerificationScopeLimited).HasDefaultValue(true);

        // String-backed, not the numeric default - same enum-pinning precedent as
        // Position.OpenOrigin/CloseOrigin and DmaIndicatorConfiguration.TickerSourceOrigin,
        // avoids a persisted value silently renumbering if TickerSourceOrigin's members are
        // ever reordered. Deliberately NO HasDefaultValue() here, same reasoning as
        // DmaIndicatorConfiguration: every current write path (AtrComputationService.
        // ComputeAsync's required parameter) already supplies an explicit origin - the
        // one-time backfill of rows that predate this column belongs in the migration's own
        // data-backfill step, not in this Fluent config.
        builder.Property(a => a.TickerSourceOrigin).HasConversion<string>();

        // Owned/migrated by Drps.Calculator. Guards against the same (Symbol, BarDate,
        // Period, CalculationVersion) row being inserted twice across separate scheduled
        // runs.
        builder.HasIndex(a => new { a.Symbol, a.BarDate, a.Period, a.CalculationVersion })
            .IsUnique();
    }
}
