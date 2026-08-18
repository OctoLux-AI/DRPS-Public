using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Calculator.Persistence.Configurations;

public class DmaCrossingLogEntryConfiguration : IEntityTypeConfiguration<DmaCrossingLogEntry>
{
    public void Configure(EntityTypeBuilder<DmaCrossingLogEntry> builder)
    {
        // String-backed, not the numeric default - same enum-pinning precedent as
        // Position.ExitReason/GateScore.Bucket, avoids a persisted value silently renumbering
        // if DmaCrossingDirection's members are ever reordered.
        builder.Property(e => e.Direction).HasConversion<string>();

        // Term is deliberately left int-backed (EF Core's default enum mapping), NOT
        // HasConversion<string>() like Direction above - a rename-only fix (2026-07-22,
        // "Rename Term-60 Alignment Concept") must not force a schema migration.
        // DmaAlignmentTerm's members already carry explicit, pinned values (5/15/30/60,
        // matching DmaCalculator.Windows/RollingDmaState.Term exactly), so reordering the enum
        // declaration can't silently renumber a stored value the way an auto-incrementing enum
        // could - the risk HasConversion<string>() exists to guard against elsewhere doesn't
        // apply here the same way. Storing as int also means this column's physical type is
        // byte-for-byte unchanged from before the rename.

        // Non-unique - a (Ticker, Term) pair legitimately flips back and forth over time, and
        // every flip gets its own append-only row.
        builder.HasIndex(e => new { e.Ticker, e.Term, e.TransitionDate });
    }
}
