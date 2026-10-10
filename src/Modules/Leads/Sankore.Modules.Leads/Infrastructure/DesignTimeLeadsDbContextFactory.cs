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
            .UseSnakeCaseNamingConvention()
            .Options;

        // Crypto.LeadFieldEncryption.Offline and not the real key: this factory exists to emit
        // DDL. It reads and writes no row, so the key it builds the converter with is irrelevant —
        // and reaching for the real one would make `dotnet ef migrations add` fail on a machine
        // that has no secrets configured.
        return new LeadsDbContext(
            opts, new FixedTenantContext(Guid.Empty), Crypto.LeadFieldEncryption.Offline);
    }
}