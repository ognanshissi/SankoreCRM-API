using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sankore.Api.Infrastructure.Audit;

/// <summary>
/// Used exclusively by EF Core design-time tools (dotnet ef migrations add …).
/// Never instantiated at runtime — the real <see cref="AuditDbContext"/> is
/// resolved from the DI container via <see cref="IDbContextFactory{TContext}"/>.
/// </summary>
internal sealed class DesignTimeAuditDbContextFactory: IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets<Program>()
            .AddEnvironmentVariables()
            .Build();

        var dbConnectionString = config.GetConnectionString("Database")!;
        
        var opts = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(
                dbConnectionString,
                b => b.MigrationsHistoryTable("__EFMigrationsHistory", "audit"))
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AuditDbContext(opts);
    }
}
