using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class ConsecutiveLossCircuitBreakerConfiguration : IEntityTypeConfiguration<ConsecutiveLossCircuitBreaker>
{
    public void Configure(EntityTypeBuilder<ConsecutiveLossCircuitBreaker> builder)
    {
        builder.Property(b => b.ConsecutiveLossCount).HasDefaultValue(0);
        builder.Property(b => b.Tripped).HasDefaultValue(false);
    }
}
