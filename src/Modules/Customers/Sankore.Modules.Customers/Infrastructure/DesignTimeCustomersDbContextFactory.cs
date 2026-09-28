using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Customers.Infrastructure;

/// <summary>
/// Used exclusively by EF Core design-time tools (dotnet ef migrations add …).
/// Never instantiated at runtime. The fixed empty tenant is deliberate: design
/// time only needs the model shape, and Guid.Empty makes it obvious that no real
/// tenant data can be reached through this context.
/// </summary>
internal sealed class DesignTimeCustomersDbContextFactory
    : IDesignTimeDbContextFactory<CustomersDbContext>
{
    public CustomersDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var opts = new DbContextOptionsBuilder<CustomersDbContext>()
            .UseNpgsql(
                config.GetConnectionString("Database"),
                o => o.MigrationsHistoryTable("__EFMigrationsHistory", "customers"))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new CustomersDbContext(opts, new FixedTenantContext(Guid.Empty));
    }
}
