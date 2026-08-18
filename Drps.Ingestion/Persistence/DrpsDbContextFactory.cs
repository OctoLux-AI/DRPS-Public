using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Drps.Ingestion.Persistence;

public class DrpsDbContextFactory : IDesignTimeDbContextFactory<DrpsDbContext>
{
    public DrpsDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DrpsDbContext>();
        optionsBuilder.UseSqlServer(@"Server=(localdb)\mssqllocaldb;Database=Drps;Trusted_Connection=True;");

        return new DrpsDbContext(optionsBuilder.Options);
    }
}
