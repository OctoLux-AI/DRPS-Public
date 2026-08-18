using Drps.Ingestion.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Drps.Tests.TestHelpers;

public static class InMemoryDbContextFactory
{
    public static DrpsDbContext Create()
    {
        var options = new DbContextOptionsBuilder<DrpsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new DrpsDbContext(options);
    }
}
