using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Calculator.Persistence.Configurations;

public class RollingDmaStateConfiguration : IEntityTypeConfiguration<RollingDmaState>
{
    public void Configure(EntityTypeBuilder<RollingDmaState> builder)
    {
        builder.Property(s => s.RollingSum).HasColumnType("decimal(18,4)");
        builder.Property(s => s.Value).HasColumnType("decimal(18,4)");

        // One live row per (Ticker, Term, CalculationVersion) - the mutable-tracker analogue
        // of DmaIndicator's own uniqueness guard, adapted for a row that's updated in place
        // rather than appended.
        builder.HasIndex(s => new { s.Ticker, s.Term, s.CalculationVersion }).IsUnique();
    }
}
