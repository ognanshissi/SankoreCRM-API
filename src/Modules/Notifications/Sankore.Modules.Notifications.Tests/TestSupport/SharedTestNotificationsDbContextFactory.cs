namespace Sankore.Modules.Notifications.Tests.TestSupport;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Notifications.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Creates NotificationsDbContext instances all backed by the same named InMemory
/// database, so multiple contexts in one test see the same data.
/// Mirrors the pattern used by TestAdminDbContextFactory.
/// </summary>
internal sealed class SharedTestNotificationsDbContextFactory(Guid tenantId) : IDisposable
{
    private readonly string _databaseName = $"notifications-tests-{Guid.NewGuid()}";

    public NotificationsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;
        return new NotificationsDbContext(options, new FixedTenantContext(tenantId));
    }

    public void Dispose() { }
}
