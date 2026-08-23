namespace Sankore.Modules.Notifications.Infrastructure;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Sankore.Shared.Kernel;

internal sealed class DesignTimeNotificationsDbContextFactory
    : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var opts = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(
                config.GetConnectionString("Database"),
                o => o.MigrationsHistoryTable("__EFMigrationsHistory", "notifications"))
            .Options;

        return new NotificationsDbContext(opts, new FixedTenantContext(Guid.Empty));
    }
}
