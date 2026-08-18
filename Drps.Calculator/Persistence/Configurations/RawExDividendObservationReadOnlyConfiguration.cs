using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Calculator.Persistence.Configurations;

/// <summary>
/// Drps.Calculator only reads RawExDividendObservation rows - the table itself is owned
/// and migrated by Drps.Ingestion. ExcludeFromMigrations keeps CalculatorDbContext's own
/// migrations from trying to (re)create a table that already exists in the shared
/// database. Same pattern as RawOhlcvBarReadOnlyConfiguration/
/// BarVerificationReadOnlyConfiguration - the type itself was already reachable from
/// Drps.Calculator via the existing Drps.Shared project reference (RawExDividendObservation
/// is a plain POCO there); only the EF Core query-side wiring is new here.
/// </summary>
public class RawExDividendObservationReadOnlyConfiguration : IEntityTypeConfiguration<RawExDividendObservation>
{
    public void Configure(EntityTypeBuilder<RawExDividendObservation> builder)
    {
        builder.ToTable("RawExDividendObservations", t => t.ExcludeFromMigrations());

        builder.Property(o => o.Value).HasColumnType("decimal(18,4)");
        builder.Property(o => o.VariancePct).HasColumnType("decimal(18,4)");
    }
}
