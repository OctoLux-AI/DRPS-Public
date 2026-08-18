using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Drps.Calculator.Persistence;

public class CalculatorDbContextFactory : IDesignTimeDbContextFactory<CalculatorDbContext>
{
    public CalculatorDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<CalculatorDbContext>();
        optionsBuilder.UseSqlServer(@"Server=(localdb)\mssqllocaldb;Database=Drps;Trusted_Connection=True;");

        return new CalculatorDbContext(optionsBuilder.Options);
    }
}
