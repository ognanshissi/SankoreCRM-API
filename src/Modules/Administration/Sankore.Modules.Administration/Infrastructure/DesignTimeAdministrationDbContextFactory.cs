using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Infrastructure;

/// <summary>
/// Used exclusively by EF Core design-time tools (dotnet ef migrations add …).
/// Never instantiated at runtime.
/// </summary>
internal sealed class DesignTimeAdministrationDbContextFactory
    : IDesignTimeDbContextFactory<AdministrationDbContext>
{
    public AdministrationDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var opts = new DbContextOptionsBuilder<AdministrationDbContext>()
            .UseNpgsql(
                config.GetConnectionString("Database"),
                o => o.MigrationsHistoryTable("__EFMigrationsHistory", "administration"))
            .Options;

        return new AdministrationDbContext(opts, new FixedTenantContext(Guid.Empty));
    }
}
