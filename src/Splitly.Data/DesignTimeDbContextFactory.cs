using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Splitly.Data;

/// <summary>Lets `dotnet ef` build the model without starting the API host (no database connection is made).</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(local);Database=Splitly;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
}
