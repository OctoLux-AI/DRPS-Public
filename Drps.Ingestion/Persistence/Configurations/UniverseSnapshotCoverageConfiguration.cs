using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class UniverseSnapshotCoverageConfiguration : IEntityTypeConfiguration<UniverseSnapshotCoverage>
{
    public void Configure(EntityTypeBuilder<UniverseSnapshotCoverage> builder)
    {
        // One row per SnapshotDate - the whole point of this table is a single, unambiguous
        // coverage fact per day, not a per-ticker one.
        builder.HasIndex(c => c.SnapshotDate).IsUnique();

        // String-backed, not the numeric default - same enum-pinning precedent as
        // Discrepancy.ResolutionMethod/OhlcvField/Position.ExitReason, avoids a persisted value
        // silently renumbering if UniverseSourceCoverage's members are ever reordered. EF Core
        // renders a combined [Flags] value as its comma-separated member names (e.g.
        // "Nasdaq, NyseAmex" for All) - readable directly in the database without decoding bits.
        builder.Property(c => c.SourcesSucceeded).HasConversion<string>();
    }
}
