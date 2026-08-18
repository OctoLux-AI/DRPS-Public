using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class UniverseSnapshotConfiguration : IEntityTypeConfiguration<UniverseSnapshot>
{
    public void Configure(EntityTypeBuilder<UniverseSnapshot> builder)
    {
        // One row per ticker per snapshot date - both the "does today's snapshot already
        // exist" cache check and the append-only-per-date invariant depend on this being
        // genuinely unique, not just conventionally true.
        builder.HasIndex(s => new { s.SnapshotDate, s.Ticker }).IsUnique();
    }
}
