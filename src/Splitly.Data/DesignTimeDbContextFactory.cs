using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore.Design;

namespace Splitly.Data;

/// <summary>Lets `dotnet ef` build the model without starting the API host (no database connection is made).</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // The Identity passkey table only exists in the model when the schema version is 3; mirror what the API configures.
        var services = new ServiceCollection()
            .Configure<IdentityOptions>(o => o.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
            .BuildServiceProvider();
        return new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=(local);Database=Splitly;Trusted_Connection=True;TrustServerCertificate=True")
            .UseApplicationServiceProvider(services)
            .Options);
    }
}
