using Drps.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Drps.Ingestion.Persistence.Configurations;

public class ExcludedTickerConfiguration : IEntityTypeConfiguration<ExcludedTicker>
{
    public void Configure(EntityTypeBuilder<ExcludedTicker> builder)
    {
        // A ticker either is or isn't excluded - never excluded multiple times.
        builder.HasIndex(t => t.Ticker).IsUnique();
    }
}
