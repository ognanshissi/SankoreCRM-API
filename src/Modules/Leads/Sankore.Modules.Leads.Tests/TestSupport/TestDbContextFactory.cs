namespace Sankore.Modules.Leads.Tests.TestSupport;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Leads.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Creates LeadsDbContext instances backed by the EF Core InMemory
/// provider, all sharing the same named database so multiple contexts in
/// a single test see the same data (mimicking multiple requests against
/// the same PostgreSQL database in production). Each context is bound to
/// a fixed ITenantContext so the module's global query filter is
/// genuinely exercised, not bypassed.
/// </summary>
public sealed class TestDbContextFactory(Guid tenantId) : IDisposable
{
    private readonly string _databaseName = $"leads-tests-{Guid.NewGuid()}";

    public LeadsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<LeadsDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;

        ITenantContext tenantContext = new FixedTenantContext(tenantId);

        return new LeadsDbContext(options, tenantContext);
    }

    public void Dispose()
    {
        // InMemory databases are garbage-collected; nothing to explicitly
        // tear down, but the method is kept for symmetry with a future
        // Testcontainers-based factory that WILL need explicit disposal.
    }
}
