using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Calculator.Persistence.Configurations;

public class RsiSlopeIndicatorConfiguration : IEntityTypeConfiguration<RsiSlopeIndicator>
{
    public void Configure(EntityTypeBuilder<RsiSlopeIndicator> builder)
    {
        builder.Property(r => r.Value).HasColumnType("decimal(18,4)");

        // String-backed, not the numeric default - same enum-pinning precedent as
        // Position.ExitReason/GateScore.Bucket/DmaCrossingLogEntry.Direction, avoids a
        // persisted value silently renumbering if SlopeConfirmationDirection's members are
        // ever reordered.
        builder.Property(r => r.ConfirmedDirection).HasConversion<string>();

        builder.Property(r => r.HasExDividendEvent).HasDefaultValue(false);
        builder.Property(r => r.HasTiingoCorrectedClose).HasDefaultValue(false);

        // Always true - see RsiSlopeIndicator's own doc comment for why. Defaulted at the DB
        // level too (not just the C# property initializer), same convention as
        // RsiIndicator.VerificationScopeLimited.
        builder.Property(r => r.VerificationScopeLimited).HasDefaultValue(true);

        // Owned/migrated by Drps.Calculator. Guards against the same (Symbol, BarDate,
        // Lookback, CalculationVersion) row being inserted twice across separate scheduled
        // runs - Lookback is part of the key (not just CalculationVersion) since it's a
        // config-driven value that can change independently of a formula-version bump.
        builder.HasIndex(r => new { r.Symbol, r.BarDate, r.Lookback, r.CalculationVersion })
            .IsUnique();
    }
}
