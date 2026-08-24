using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Leads.Infrastructure;

internal sealed class DesignTimeLeadsDbContextFactory: IDesignTimeDbContextFactory<LeadsDbContext>
{
    public LeadsDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var opts = new DbContextOptionsBuilder<LeadsDbContext>()
            .UseNpgsql(
                config.GetConnectionString("Database"),
                o => o.MigrationsHistoryTable("__EFMigrationsHistory", "leads"))
            .Options;

        return new LeadsDbContext(opts, new FixedTenantContext(Guid.Empty));
    }
}