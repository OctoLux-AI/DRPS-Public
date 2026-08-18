using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class RawFieldObservationConfiguration : IEntityTypeConfiguration<RawFieldObservation>
{
    public void Configure(EntityTypeBuilder<RawFieldObservation> builder)
    {
        builder.Property(f => f.Value).HasColumnType("decimal(18,9)");
    }
}
