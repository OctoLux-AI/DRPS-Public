using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class WeeklyVarianceAuditEntryConfiguration : IEntityTypeConfiguration<WeeklyVarianceAuditEntry>
{
    public void Configure(EntityTypeBuilder<WeeklyVarianceAuditEntry> builder)
    {
        builder.Property(e => e.AlpacaValue).HasColumnType("decimal(18,4)");
        builder.Property(e => e.TiingoValue).HasColumnType("decimal(18,4)");
        builder.Property(e => e.AbsoluteVariance).HasColumnType("decimal(18,4)");
        builder.Property(e => e.PercentVariance).HasColumnType("decimal(9,6)");

        // String-backed, not the numeric default - same enum-pinning precedent as
        // Discrepancy.ResolutionMethod/Position.ExitReason, avoids a persisted value silently
        // renumbering if OhlcvField's members are ever reordered.
        builder.Property(e => e.Field).HasConversion<string>();

        // Query shape this table exists to serve: "show me the variance trend for ticker X,
        // field Y, across weeks" - not unique, since re-running a week intentionally appends
        // duplicate rows rather than deduplicating (see the entity's own doc comment).
        builder.HasIndex(e => new { e.Ticker, e.Field, e.WeekEndingDate });
    }
}
