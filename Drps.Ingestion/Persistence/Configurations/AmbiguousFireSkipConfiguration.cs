using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class AmbiguousFireSkipConfiguration : IEntityTypeConfiguration<AmbiguousFireSkip>
{
    public void Configure(EntityTypeBuilder<AmbiguousFireSkip> builder)
    {
        // Not unique on Ticker alone, unlike ExcludedTicker - a ticker can legitimately
        // accumulate multiple rows over time (one per AmbiguousUnresolved occurrence), each
        // independently consumed. The lookup this table actually needs (an unconsumed row for a
        // given ticker) is served by this composite index.
        builder.HasIndex(s => new { s.Ticker, s.ConsumedAt });
    }
}
