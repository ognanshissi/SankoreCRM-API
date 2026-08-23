using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Sankore.Api.Infrastructure.Audit;

/// <summary>
/// Used exclusively by EF Core design-time tools (dotnet ef migrations add …).
/// Never instantiated at runtime — the real <see cref="AuditDbContext"/> is
/// resolved from the DI container via <see cref="IDbContextFactory{TContext}"/>.
/// </summary>
internal sealed class DesignTimeAuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<AuditDbContext>()
            .UseNpgsql(
                "Host=localhost;Port=5432;Database=sankore_crm_dev;Username=sankore_app;Password=devpassword",
                b => b.MigrationsHistoryTable("__EFMigrationsHistory", "audit"))
            .Options;

        return new AuditDbContext(opts);
    }
}
