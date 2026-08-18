using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class KillSwitchCounterConfiguration : IEntityTypeConfiguration<KillSwitchCounter>
{
    public void Configure(EntityTypeBuilder<KillSwitchCounter> builder)
    {
        builder.Property(k => k.OpenAttemptCount).HasDefaultValue(0);
    }
}
