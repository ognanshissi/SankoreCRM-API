namespace Sankore.Modules.Notifications.Tests.TestSupport;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Shared.Kernel;

internal static class TestNotificationsDbContextFactory
{
    public static NotificationsDbContext Create(Guid? tenantId = null)
    {
        var tenant = new FixedTenantContext(tenantId ?? Guid.NewGuid());

        var opts = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new NotificationsDbContext(opts, tenant);
    }
}
