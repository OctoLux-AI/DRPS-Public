using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Calculator.Persistence.Configurations;

public class RsiConcavityIndicatorConfiguration : IEntityTypeConfiguration<RsiConcavityIndicator>
{
    public void Configure(EntityTypeBuilder<RsiConcavityIndicator> builder)
    {
        builder.Property(r => r.Value).HasColumnType("decimal(18,4)");

        // String-backed - same enum-pinning precedent as RsiSlopeIndicator.ConfirmedDirection.
        builder.Property(r => r.ConfirmedDirection).HasConversion<string>();

        builder.Property(r => r.HasExDividendEvent).HasDefaultValue(false);
        builder.Property(r => r.HasTiingoCorrectedClose).HasDefaultValue(false);

        // Always true - see RsiConcavityIndicator's own doc comment for why.
        builder.Property(r => r.VerificationScopeLimited).HasDefaultValue(true);

        // Owned/migrated by Drps.Calculator. Guards against the same (Symbol, BarDate,
        // SlopeLookback, CalculationVersion) row being inserted twice across separate scheduled
        // runs.
        builder.HasIndex(r => new { r.Symbol, r.BarDate, r.SlopeLookback, r.CalculationVersion })
            .IsUnique();
    }
}
