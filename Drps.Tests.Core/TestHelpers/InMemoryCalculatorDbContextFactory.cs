using Drps.Calculator.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Drps.Tests.TestHelpers;

public static class InMemoryCalculatorDbContextFactory
{
    public static CalculatorDbContext Create()
    {
        var options = new DbContextOptionsBuilder<CalculatorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new CalculatorDbContext(options);
    }
}
