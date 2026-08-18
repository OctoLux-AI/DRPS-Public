using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Calculator.Persistence.Configurations;

/// <summary>
/// Drps.Calculator only reads UniverseSnapshotCoverage rows - the table itself is owned and
/// migrated by Drps.Ingestion (UniverseSnapshotCoverageConfiguration). Same ExcludeFromMigrations
/// pattern as UniverseSnapshotReadOnlyConfiguration, so Calculator's own migrations never try to
/// (re)create a table that already exists in the shared database.
/// </summary>
public class UniverseSnapshotCoverageReadOnlyConfiguration : IEntityTypeConfiguration<UniverseSnapshotCoverage>
{
    public void Configure(EntityTypeBuilder<UniverseSnapshotCoverage> builder)
    {
        builder.ToTable("UniverseSnapshotCoverages", t => t.ExcludeFromMigrations());

        builder.HasIndex(c => c.SnapshotDate).IsUnique();

        builder.Property(c => c.SourcesSucceeded).HasConversion<string>();
    }
}
