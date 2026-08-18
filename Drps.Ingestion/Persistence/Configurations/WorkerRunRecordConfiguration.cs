using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class WorkerRunRecordConfiguration : IEntityTypeConfiguration<WorkerRunRecord>
{
    public void Configure(EntityTypeBuilder<WorkerRunRecord> builder)
    {
        // Speeds the idempotency guard's own (WorkerName, RunDate) existence check - the only
        // query this table is ever read with.
        builder.HasIndex(r => new { r.WorkerName, r.RunDate });

        // Matches the entity's own C# default (true) - every pre-existing row (and any future
        // insert path that bypasses WorkerRunGuard.RecordSuccessfulRunAsync's explicit
        // parameter) backfills/defaults to "succeeded", consistent with how these rows were
        // always effectively treated before this column existed. Same precedent as
        // AdjusterParametersConfiguration.MaxConcurrentPositions's HasDefaultValue(15).
        builder.Property(r => r.AllSourcesSucceeded).HasDefaultValue(true);
    }
}
