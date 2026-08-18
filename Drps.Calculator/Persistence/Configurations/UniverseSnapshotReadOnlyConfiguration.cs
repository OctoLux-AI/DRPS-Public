using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Calculator.Persistence.Configurations;

/// <summary>
/// Drps.Calculator only reads UniverseSnapshot rows - the table itself is owned and migrated
/// by Drps.Ingestion (UniverseSnapshotConfiguration). Same ExcludeFromMigrations pattern as
/// RawOhlcvBarReadOnlyConfiguration, so Calculator's own migrations never try to (re)create a
/// table that already exists in the shared database.
/// </summary>
public class UniverseSnapshotReadOnlyConfiguration : IEntityTypeConfiguration<UniverseSnapshot>
{
    public void Configure(EntityTypeBuilder<UniverseSnapshot> builder)
    {
        builder.ToTable("UniverseSnapshots", t => t.ExcludeFromMigrations());

        builder.HasIndex(s => new { s.SnapshotDate, s.Ticker }).IsUnique();
    }
}
