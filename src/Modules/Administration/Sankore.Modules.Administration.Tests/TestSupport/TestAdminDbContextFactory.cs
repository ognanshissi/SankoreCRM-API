namespace Sankore.Modules.Administration.Tests.TestSupport;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Creates AdministrationDbContext instances backed by the EF Core InMemory
/// provider, all sharing the same named database so multiple contexts in a
/// single test see the same data. Each context is bound to a fixed
/// ITenantContext so global query filters are genuinely exercised.
/// </summary>
public sealed class TestAdminDbContextFactory(Guid tenantId) : IDisposable
{
    private readonly string _databaseName = $"admin-tests-{Guid.NewGuid()}";

    public AdministrationDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AdministrationDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;

        return new AdministrationDbContext(options, new FixedTenantContext(tenantId));
    }

    public void Dispose()
    {
        // InMemory databases are garbage-collected; nothing to tear down,
        // but kept for symmetry with a future Testcontainers-based factory.
    }
}
