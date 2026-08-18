using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class RawOhlcvBarConfiguration : IEntityTypeConfiguration<RawOhlcvBar>
{
    public void Configure(EntityTypeBuilder<RawOhlcvBar> builder)
    {
        builder.Property(b => b.Open).HasColumnType("decimal(18,4)");
        builder.Property(b => b.High).HasColumnType("decimal(18,4)");
        builder.Property(b => b.Low).HasColumnType("decimal(18,4)");
        builder.Property(b => b.Close).HasColumnType("decimal(18,4)");
    }
}
