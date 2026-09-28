namespace Sankore.Modules.Customers.Tests.TestSupport;

using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Customers.Infrastructure;
using Sankore.Shared.Kernel;

/// <summary>
/// Creates <see cref="CustomersDbContext"/> instances over the EF Core InMemory
/// provider, all sharing one named database so several contexts inside a single test
/// see the same rows — the way several requests do against PostgreSQL in production.
///
/// Each context is bound to a real <see cref="FixedTenantContext"/> rather than a
/// bypass, so the module's global query filters are genuinely exercised by the tests:
/// a handler that forgets its tenant predicate still fails here.
///
/// Caveat to keep in mind when writing assertions: the InMemory provider enforces
/// neither unique indexes nor filtered indexes. Tests must not rely on it to prove
/// uniqueness (client number, blind index, dedup key) — that belongs to the migration
/// and to the handler's explicit pre-check.
/// </summary>
public sealed class TestCustomersDbContextFactory(Guid tenantId) : IDisposable
{
    private readonly string _databaseName = $"customers-tests-{Guid.NewGuid()}";

    public Guid TenantId { get; } = tenantId;

    public CustomersDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CustomersDbContext>()
            .UseInMemoryDatabase(_databaseName)
            .Options;

        ITenantContext tenantContext = new FixedTenantContext(TenantId);

        return new CustomersDbContext(options, tenantContext);
    }

    public void Dispose()
    {
        // InMemory databases are garbage-collected with the last context that used
        // them; the method is kept so tests can adopt `using` now and a future
        // container-backed factory can add real teardown without touching them.
    }
}
